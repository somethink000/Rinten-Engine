# Sourced by the Android scripts: sets NDK from ANDROID_NDK_HOME/ROOT, else the newest ndk/ in an Android SDK -
# the one the editor's Build page installs (ExportToolchain.AndroidSdkInstall) included. Keep LF line endings.

NDK="${ANDROID_NDK_HOME:-$ANDROID_NDK_ROOT}"

if [ -z "$NDK" ]; then
	for sdk in "$ANDROID_HOME" "$ANDROID_SDK_ROOT" "${XDG_DATA_HOME:-$HOME/.local/share}/Rinten/android-sdk" "$HOME/Android/Sdk"; do
		[ -n "$sdk" ] && [ -d "$sdk/ndk" ] || continue
		NDK="$(ls -d "$sdk"/ndk/*/ 2>/dev/null | sort -V | tail -1)"
		NDK="${NDK%/}"
		[ -n "$NDK" ] && break
	done
fi

if [ -z "$NDK" ]; then
	echo "ERROR: no Android NDK." >&2
	echo "Install one into the Android SDK (sdkmanager \"ndk;27.3.13750724\") or set ANDROID_NDK_HOME to it." >&2
	exit 1
fi
