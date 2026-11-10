import Foundation

/// The quota requests over URLSession: one ephemeral session per request with no cookie store, cache or stored
/// credentials; the system proxy for vendors and none for loopback; redirects refused; a self-signed certificate
/// accepted from 127.0.0.1 alone; `Content-Length` and the stream held to the cap.
public struct QuotaURLSessionTransport: QuotaHTTPTransport {
    let protocolClasses: [AnyClass]

    public init() {
        protocolClasses = []
    }

    init(protocolClasses: [AnyClass]) {
        self.protocolClasses = protocolClasses
    }

    public func send(_ request: QuotaHTTPRequest) async throws(QuotaHTTPError) -> QuotaHTTPResponse {
        guard QuotaHosts.permits(request.url) else { throw .disallowedURL }
        var urlRequest = URLRequest(url: request.url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: request.timeout)
        urlRequest.httpMethod = request.method.rawValue
        urlRequest.httpShouldHandleCookies = false
        // CFNetwork would add the user's languages; a constant says nothing about them.
        urlRequest.setValue("*", forHTTPHeaderField: "Accept-Language")
        for header in request.headers { urlRequest.setValue(header.value, forHTTPHeaderField: header.name) }
        urlRequest.httpBody = request.body
        let delegate = QuotaFetchDelegate(request: request)
        let session = URLSession(configuration: configuration(request), delegate: delegate, delegateQueue: delegate.queue)
        do {
            return try await delegate.run(urlRequest, session: session)
        } catch let error as QuotaHTTPError {
            throw error
        } catch {
            throw .transport("cancelled")
        }
    }

    private func configuration(_ request: QuotaHTTPRequest) -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.httpCookieAcceptPolicy = .never
        config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.httpAdditionalHeaders = [:]
        config.timeoutIntervalForRequest = request.timeout
        config.timeoutIntervalForResource = request.timeout
        config.waitsForConnectivity = false
        config.tlsMinimumSupportedProtocolVersion = .TLSv12
        if request.isLoopback { config.connectionProxyDictionary = [:] }
        if !protocolClasses.isEmpty { config.protocolClasses = protocolClasses }
        return config
    }
}

/// Delegate of one request; its mutable state is only touched on its serial queue.
private final class QuotaFetchDelegate: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let queue: OperationQueue
    private let request: QuotaHTTPRequest
    private var buffer = Data()
    private var response: HTTPURLResponse?
    private var failure: QuotaHTTPError?
    private var continuation: CheckedContinuation<QuotaHTTPResponse, any Error>?

    init(request: QuotaHTTPRequest) {
        self.request = request
        queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        queue.name = "io.github.zoefix.agentspeed.quota"
    }

    func run(_ urlRequest: URLRequest, session: URLSession) async throws -> QuotaHTTPResponse {
        let task = session.dataTask(with: urlRequest)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.addOperation {
                    self.continuation = continuation
                    task.resume()
                }
            }
        } onCancel: {
            task.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        failure = .redirected
        completionHandler(nil)
        task.cancel()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        let space = challenge.protectionSpace
        guard space.authenticationMethod == NSURLAuthenticationMethodServerTrust else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        if request.isLoopback, space.host == QuotaHosts.loopback, let trust = space.serverTrust {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.performDefaultHandling, nil)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse, http.url.map({ $0.host() == request.url.host() }) ?? false else {
            failure = .disallowedURL
            completionHandler(.cancel)
            return
        }
        self.response = http
        if (300..<400).contains(http.statusCode) {
            failure = .redirected
            completionHandler(.cancel)
            return
        }
        guard (200..<300).contains(http.statusCode) else {
            completionHandler(.cancel)
            return
        }
        let encoded = http.value(forHTTPHeaderField: "Content-Encoding").map { $0.lowercased() != "identity" } ?? false
        if !encoded, http.expectedContentLength > Int64(request.maxBytes) {
            failure = .tooLarge
            completionHandler(.cancel)
            return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        guard buffer.count + data.count <= request.maxBytes else {
            failure = .tooLarge
            buffer = Data()
            dataTask.cancel()
            return
        }
        buffer.append(data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        session.finishTasksAndInvalidate()
        let result: Result<QuotaHTTPResponse, any Error>
        let date = response?.value(forHTTPHeaderField: "Date").flatMap(CachedResponseInfo.dateMs)
        if let failure {
            result = .failure(failure)
        } else if let response, !(200..<300).contains(response.statusCode) {
            result = .success(QuotaHTTPResponse(status: response.statusCode, dateMs: date))
        } else if let error {
            result = .failure(QuotaHTTPError.transport((error as? URLError).map { "URLError \($0.code.rawValue)" } ?? "transport error"))
        } else if let response {
            result = .success(QuotaHTTPResponse(status: response.statusCode, body: buffer, dateMs: date))
        } else {
            result = .failure(QuotaHTTPError.transport("no response"))
        }
        buffer = Data()
        continuation?.resume(with: result)
        continuation = nil
    }
}
