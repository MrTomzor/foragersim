#!/usr/bin/env bash
# One-time setup for running a headless Unity build on a Linux box.
#
# Configures a virtual X server that Unity can attach to. Prefers the NVIDIA
# driver (GPU-accelerated) when present, falls back to the xorg-dummy driver
# (CPU via Mesa llvmpipe) otherwise.
#
# Installs packages, writes /etc/X11/xorg-ratsim.conf, and installs a systemd
# unit that runs `Xorg :99` on boot. After running this once, start_ratsim_headless.sh
# can launch the Unity binary against DISPLAY=:99 without sudo.
#
# Usage:
#   sudo ./setup_headless_display.sh
set -eu

if [[ $EUID -ne 0 ]]; then
  echo "this script needs sudo"
  exit 2
fi

XORG_CONF=/etc/X11/xorg-ratsim.conf
UNIT_PATH=/etc/systemd/system/xorg-ratsim.service

if command -v nvidia-smi >/dev/null && nvidia-smi >/dev/null 2>&1; then
  echo "nvidia driver detected — using GPU-accelerated headless X"
  apt-get update
  # X-side nvidia driver is separate from the kernel driver; install explicitly.
  NVIDIA_VER=$(nvidia-smi --query-gpu=driver_version --format=csv,noheader | cut -d. -f1)
  apt-get install -y xserver-xorg-core "xserver-xorg-video-nvidia-${NVIDIA_VER}" || \
    apt-get install -y xserver-xorg-core xserver-xorg-video-nvidia

  # Find the NVIDIA GPU's PCI bus so Xorg can bind to it on a headless host.
  PCI=$(lspci -D | awk '/NVIDIA/ {print $1; exit}')
  if [[ -z "$PCI" ]]; then
    echo "could not find NVIDIA PCI device"
    exit 1
  fi
  # Convert "0000:00:06.0" to Xorg's "PCI:0:6:0".
  BUSID=$(echo "$PCI" | awk -F'[:.]' '{printf "PCI:%d:%d:%d", strtonum("0x"$2), strtonum("0x"$3), strtonum("0x"$4)}')
  echo "using BusID $BUSID"

  cat > "$XORG_CONF" <<EOF
Section "ServerLayout"
    Identifier "Layout0"
    Screen 0 "Screen0"
EndSection

Section "Device"
    Identifier "Device0"
    Driver "nvidia"
    BusID "$BUSID"
    Option "AllowEmptyInitialConfiguration" "true"
    Option "UseDisplayDevice" "none"
    Option "ConnectedMonitor" "DFP-0"
EndSection

Section "Screen"
    Identifier "Screen0"
    Device "Device0"
    DefaultDepth 24
    SubSection "Display"
        Depth 24
        Virtual 1280 720
    EndSubSection
EndSection
EOF

else
  echo "no nvidia driver — using CPU (Mesa llvmpipe) headless X"
  apt-get update
  apt-get install -y xserver-xorg-core xserver-xorg-video-dummy \
                     libgl1-mesa-dri libglu1-mesa

  cat > "$XORG_CONF" <<'EOF'
Section "Device"
    Identifier "Dummy"
    Driver "dummy"
    VideoRam 256000
EndSection

Section "Monitor"
    Identifier "Monitor0"
    HorizSync 28.0-80.0
    VertRefresh 48.0-75.0
    Modeline "1280x720" 74.48 1280 1336 1472 1664 720 721 724 746
EndSection

Section "Screen"
    Identifier "Screen0"
    Device "Dummy"
    Monitor "Monitor0"
    DefaultDepth 24
    SubSection "Display"
        Depth 24
        Modes "1280x720"
    EndSubSection
EndSection

Section "ServerLayout"
    Identifier "Layout0"
    Screen 0 "Screen0"
EndSection
EOF

fi

# Xorg on Ubuntu refuses to run as non-root by default. Allow "anybody" so the
# launcher script can read /tmp/.X11-unix/X99 without sudo.
if [[ -f /etc/X11/Xwrapper.config ]]; then
  sed -i 's/^allowed_users=.*/allowed_users=anybody/' /etc/X11/Xwrapper.config || true
  grep -q '^allowed_users=' /etc/X11/Xwrapper.config \
    || echo 'allowed_users=anybody' >> /etc/X11/Xwrapper.config
  grep -q '^needs_root_rights=' /etc/X11/Xwrapper.config \
    || echo 'needs_root_rights=yes' >> /etc/X11/Xwrapper.config
fi

cat > "$UNIT_PATH" <<EOF
[Unit]
Description=Headless Xorg on :99 for ratsim
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/Xorg :99 -config $XORG_CONF -nolisten tcp vt1
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now xorg-ratsim.service
sleep 2

if systemctl is-active --quiet xorg-ratsim.service; then
  echo "Xorg running on :99"
  echo "test with: DISPLAY=:99 glxinfo | grep -E 'renderer|OpenGL version'"
else
  echo "Xorg failed to start; check: journalctl -u xorg-ratsim -n 50"
  exit 1
fi
