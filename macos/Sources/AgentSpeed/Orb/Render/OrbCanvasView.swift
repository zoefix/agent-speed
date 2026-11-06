import AppKit
import Metal
import QuartzCore

/// A transparent Metal surface for an orb's dynamic layers, drawn by the shared `OrbFrameClock` while its request
/// asks for frames and on demand otherwise (a static orb keeps its last frame and draws nothing).
@MainActor
final class OrbCanvasView: NSView, OrbFrameClient {
    /// Fills the scene for a frame: canvas size (points), pixels per point, time.
    var sceneProvider: ((inout OrbScene, CGSize, CGFloat, TimeInterval) -> Void)?
    /// Called after every rendered frame (the animator re-evaluates its demand).
    var onFrame: (() -> Void)?
    /// Another canvas drawn in this one's command buffer on each of its frames (a board's warp under its rows): two
    /// surfaces, one submission. The companion asks for no frames of its own meanwhile.
    weak var companion: OrbCanvasView?

    var request: OrbFrameRequest = .none {
        didSet { if request != oldValue { OrbFrameClock.shared.update() } }
    }

    /// CPU time spent building and encoding frames, and their count (benchmarks).
    private(set) var cpuNanoseconds: UInt64 = 0
    private(set) var framesDrawn = 0
    let encoder: OrbEncoder?

    /// Every canvas presents in step with the display's refresh (`AppSettings.displaySync`); a canvas follows a change
    /// at its next frame.
    static var displaySync = true

    private var scene = OrbScene(capacity: 2048)
    private var presentsInSync = OrbCanvasView.displaySync
    private var metalLayer: CAMetalLayer? { layer as? CAMetalLayer }
    nonisolated(unsafe) private var screenObserver: NSObjectProtocol?
    private var pendingDraw = false
    private let pass: MTLRenderPassDescriptor = {
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
        return pass
    }()

    override init(frame frameRect: NSRect) {
        encoder = OrbGPU.shared.map(OrbEncoder.init)
        super.init(frame: frameRect)
        wantsLayer = true
        layerContentsRedrawPolicy = .never
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    override func makeBackingLayer() -> CALayer {
        let layer = CAMetalLayer()
        layer.device = OrbGPU.shared?.device
        layer.pixelFormat = OrbGPU.pixelFormat
        layer.framebufferOnly = true
        layer.isOpaque = false
        layer.colorspace = CGColorSpace(name: CGColorSpace.sRGB)
        layer.maximumDrawableCount = 3
        layer.displaySyncEnabled = presentsInSync
        layer.allowsNextDrawableTimeout = true
        return layer
    }

    override var isOpaque: Bool { false }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    var frameRequest: OrbFrameRequest {
        guard window != nil, !isHiddenOrHasHiddenAncestor else { return .none }
        return request
    }

    var frameScreen: NSScreen? { window?.screen }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let screenObserver { NotificationCenter.default.removeObserver(screenObserver) }
        screenObserver = nil
        if let window {
            OrbFrameClock.shared.register(self)
            screenObserver = NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeScreenNotification, object: window, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.updateDrawableSize()
                    OrbFrameClock.shared.update()
                }
            }
            updateDrawableSize()
            setNeedsFrame()
        } else {
            OrbFrameClock.shared.unregister(self)
        }
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        updateDrawableSize()
        setNeedsFrame()
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        updateDrawableSize()
    }

    private var backingScale: CGFloat { window?.backingScaleFactor ?? 2 }

    private func updateDrawableSize() {
        guard let metalLayer else { return }
        let scale = backingScale
        metalLayer.contentsScale = scale
        let size = CGSize(width: (bounds.width * scale).rounded(), height: (bounds.height * scale).rounded())
        if metalLayer.drawableSize != size, size.width > 0, size.height > 0 { metalLayer.drawableSize = size }
    }

    /// Draws one frame soon (static content changed); coalesced within the run loop turn.
    func setNeedsFrame() {
        guard !pendingDraw else { return }
        pendingDraw = true
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.pendingDraw = false
                guard self.window != nil else { return }
                self.renderFrame(at: Clock.now)
            }
        }
    }

    func renderFrame(at time: TimeInterval) {
        guard let encoder, sceneProvider != nil, bounds.width > 0, let metalLayer,
              let drawable = nextDrawable(of: metalLayer) else { return }
        let start = DispatchTime.now().uptimeNanoseconds
        guard let commandBuffer = encoder.beginFrame() else { return }
        encode(drawable, at: time, into: commandBuffer, joining: false)
        companion?.renderFrame(at: time, joining: commandBuffer)
        commandBuffer.commit()
        cpuNanoseconds &+= DispatchTime.now().uptimeNanoseconds - start
        onFrame?()
    }

    /// Draws a frame inside another canvas's command buffer (see `companion`); its CPU and GPU time count as the
    /// owner's.
    private func renderFrame(at time: TimeInterval, joining commandBuffer: MTLCommandBuffer) {
        guard window != nil, sceneProvider != nil, bounds.width > 0, let metalLayer,
              let drawable = nextDrawable(of: metalLayer) else { return }
        encode(drawable, at: time, into: commandBuffer, joining: true)
    }

    private func nextDrawable(of layer: CAMetalLayer) -> CAMetalDrawable? {
        if presentsInSync != Self.displaySync {
            presentsInSync = Self.displaySync
            layer.displaySyncEnabled = presentsInSync
        }
        return layer.nextDrawable()
    }

    private func encode(_ drawable: CAMetalDrawable, at time: TimeInterval, into commandBuffer: MTLCommandBuffer, joining: Bool) {
        guard let encoder, let sceneProvider else { return }
        let scale = backingScale
        sceneProvider(&scene, bounds.size, scale, time)
        pass.colorAttachments[0].texture = drawable.texture
        if joining {
            encoder.render(scene, size: bounds.size, scale: scale, pass: pass, present: drawable, joining: commandBuffer)
        } else {
            encoder.encode(count: 1, size: bounds.size, scale: scale, pass: pass, present: drawable, into: commandBuffer) { _ in
                (scene, CGRect(origin: .zero, size: bounds.size), nil)
            }
        }
        framesDrawn += 1
        pass.colorAttachments[0].texture = nil
    }

    func resetStats() {
        cpuNanoseconds = 0
        framesDrawn = 0
    }

    deinit {
        if let screenObserver { NotificationCenter.default.removeObserver(screenObserver) }
    }
}
