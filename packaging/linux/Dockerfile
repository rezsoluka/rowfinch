ARG UBUNTU_VERSION=24.04
FROM ubuntu:${UBUNTU_VERSION}
ARG PACKAGE=rowfinch_0.2.0~beta.1_amd64.deb
ARG UPGRADE_PACKAGE=${PACKAGE}
ENV DEBIAN_FRONTEND=noninteractive
COPY artifacts/packages/${PACKAGE} /packages/rowfinch.deb
COPY artifacts/packages/${UPGRADE_PACKAGE} /packages/upgrade.deb
COPY packaging/linux/test-installed.sh /test-installed.sh
RUN apt-get update \
    && apt-get install -y --no-install-recommends /packages/rowfinch.deb xvfb xauth dbus-daemon dbus-x11 x11-utils desktop-file-utils \
    && ! command -v dotnet \
    && bash /test-installed.sh
# Reinstall and removal must leave user data alone; no host files are mounted.
RUN mkdir -p /root/.config/rowfinch \
    && printf 'test sentinel\n' > /root/.config/rowfinch/package-test-sentinel \
    && apt-get install -y --no-install-recommends --reinstall /packages/rowfinch.deb \
    && apt-get install -y --no-install-recommends /packages/upgrade.deb \
    && bash /test-installed.sh \
    && apt-get remove -y rowfinch \
    && test ! -e /usr/bin/rowfinch \
    && test ! -e /usr/share/applications/rowfinch.desktop \
    && test ! -e /usr/lib/rowfinch \
    && test -s /root/.config/rowfinch/package-test-sentinel \
    && apt-get install -y --no-install-recommends /packages/rowfinch.deb \
    && apt-get purge -y rowfinch \
    && test ! -e /usr/share/icons/hicolor/scalable/apps/rowfinch.svg \
    && test ! -e /usr/share/icons/hicolor/256x256/apps/rowfinch.png \
    && test -s /root/.config/rowfinch/package-test-sentinel
