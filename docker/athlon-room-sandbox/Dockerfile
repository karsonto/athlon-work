# Athlon room sandbox: Python 3.12 + JDK 8 + Node 24 + Chromium
# CI publishes karsonto/athlon-room-sandbox:2 (linux/amd64 + linux/arm64).
FROM python:3.12-slim-bookworm

ARG TARGETARCH

ENV DEBIAN_FRONTEND=noninteractive \
    TZ=Asia/Shanghai \
    JAVA_HOME=/opt/java/openjdk \
    PATH="/opt/java/openjdk/bin:/usr/local/bin:${PATH}" \
    ATHLON_CHROME_BIN=chromium \
    ATHLON_CDP=http://127.0.0.1:9222 \
    ATHLON_CHROME_PROFILE=/tmp/athlon-chrome \
    HOME=/tmp/athlon-home

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git bash coreutils findutils grep sed gawk xz-utils tzdata \
        chromium fonts-liberation fonts-noto-core \
    && ln -snf /usr/share/zoneinfo/Asia/Shanghai /etc/localtime \
    && echo Asia/Shanghai >/etc/timezone \
    && curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && mkdir -p /opt/java /opt/athlon \
    && arch="$TARGETARCH" \
    && if [ -z "$arch" ]; then \
         case "$(uname -m)" in \
           x86_64) arch=amd64 ;; \
           aarch64|arm64) arch=arm64 ;; \
           *) arch=amd64 ;; \
         esac; \
       fi \
    && case "$arch" in \
         amd64) jdk_arch=x64 ;; \
         arm64) jdk_arch=aarch64 ;; \
         *) jdk_arch=x64 ;; \
       esac \
    && curl -fsSL "https://github.com/adoptium/temurin8-binaries/releases/download/jdk8u432-b06/OpenJDK8U-jdk_${jdk_arch}_linux_hotspot_8u432b06.tar.gz" \
        | tar -xz -C /opt/java \
    && ln -s /opt/java/jdk8u432-b06 /opt/java/openjdk \
    && pip install --no-cache-dir numpy pandas openpyxl python-docx Pillow websocket-client \
    && useradd --create-home --uid 10001 --shell /bin/bash athlon \
    && mkdir -p /workspace /skills /tmp/athlon /tmp/athlon-home /tmp/athlon-chrome \
    && chown -R athlon:athlon /workspace /tmp/athlon /tmp/athlon-home /tmp/athlon-chrome /opt/athlon \
    && chmod 755 /skills \
    && ln -sf /usr/local/bin/python3 /usr/local/bin/python \
    && (command -v chromium >/dev/null || ln -sf /usr/bin/chromium-browser /usr/local/bin/chromium) \
    && rm -rf /var/lib/apt/lists/*

COPY browser_ctl.py /opt/athlon/browser_ctl.py
RUN chmod 755 /opt/athlon/browser_ctl.py \
    && chown athlon:athlon /opt/athlon/browser_ctl.py

WORKDIR /workspace
USER athlon

CMD ["sleep", "infinity"]
