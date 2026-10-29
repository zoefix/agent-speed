# Build and test environment for the Linux version. Ubuntu 22.04 is the glibc baseline of the AppImage.
FROM ubuntu:22.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates git python3 pkg-config cmake ninja-build g++-12 gcc-12 \
        libzstd-dev libsqlite3-dev libssl-dev libpcre2-dev libicu-dev tzdata \
    && rm -rf /var/lib/apt/lists/*
ENV CC=gcc-12 CXX=g++-12
WORKDIR /src
