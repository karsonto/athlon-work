# Athlon room sandbox: Python 3.12 + JDK 8 + Node 20 + Chromium (linux/amd64)
FROM python:3.12-slim-bookworm

ENV DEBIAN_FRONTEND=noninteractive \
    JAVA_HOME=/opt/java/openjdk \
    PATH="/opt/java/openjdk/bin:/usr/local/bin:${PATH}" \
    ATHLON_CHROME_BIN=chromium \
    ATHLON_CDP=http://127.0.0.1:9222 \
    ATHLON_CHROME_PROFILE=/tmp/athlon-chrome \
    HOME=/tmp/athlon-home

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git bash coreutils findutils grep sed gawk xz-utils \
        chromium fonts-liberation fonts-noto-core \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && mkdir -p /opt/java /opt/athlon \
    && curl -fsSL "https://github.com/adoptium/temurin8-binaries/releases/download/jdk8u432-b06/OpenJDK8U-jdk_x64_linux_hotspot_8u432b06.tar.gz" \
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
