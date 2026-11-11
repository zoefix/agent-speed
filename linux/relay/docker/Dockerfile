# The static musl toolchain of the relay (REMOTE 3.1): Alpine 3.20 with the static archives of the core's libraries
# and mbedTLS 3 for the quota requests (REMOTE 11.4).
# BASE names the architecture's own repository (amd64/alpine, arm64v8/alpine), which every builder honours.
ARG BASE=alpine:3.20
FROM ${BASE}
RUN apk add --no-cache build-base cmake samurai pkgconf linux-headers \
        zstd-dev zstd-static brotli-dev brotli-static sqlite-dev sqlite-static zlib-dev zlib-static openssl-dev openssl-libs-static \
        mbedtls-dev mbedtls-static
