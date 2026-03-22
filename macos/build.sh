#!/bin/bash
set -e

APP_NAME="WhisperGate"
BUILD_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_DIR="$BUILD_DIR/build"
APP_BUNDLE="$OUTPUT_DIR/$APP_NAME.app"
SOURCES_DIR="$BUILD_DIR/Sources"
RESOURCES_DIR="$BUILD_DIR/Resources"

echo "=== Building $APP_NAME ==="

if ! command -v swiftc &> /dev/null; then
    echo "Error: Swift compiler not found."
    echo "Install Xcode Command Line Tools: xcode-select --install"
    exit 1
fi

echo "Swift: $(swiftc --version 2>&1 | head -1)"

# Clean
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

# Resources
cp "$RESOURCES_DIR/Info.plist" "$APP_BUNDLE/Contents/"
echo -n "APPL????" > "$APP_BUNDLE/Contents/PkgInfo"

# Compile
SOURCES=$(find "$SOURCES_DIR" -name "*.swift" -type f)
echo "Compiling $(echo "$SOURCES" | wc -l | tr -d ' ') Swift files (universal binary)..."

SDK_PATH="$(xcrun --show-sdk-path)"
SWIFT_FLAGS="-parse-as-library -sdk $SDK_PATH \
    -framework SwiftUI -framework AppKit -framework AVFoundation \
    -framework CoreAudio -framework AudioToolbox -framework Accelerate \
    -framework ServiceManagement \
    -O -whole-module-optimization"

# Build for arm64
swiftc -o "$OUTPUT_DIR/WhisperGate_arm64" \
    -target arm64-apple-macosx14.0 \
    $SWIFT_FLAGS $SOURCES

# Build for x86_64
swiftc -o "$OUTPUT_DIR/WhisperGate_x86_64" \
    -target x86_64-apple-macosx14.0 \
    $SWIFT_FLAGS $SOURCES

# Combine into universal binary
lipo -create \
    "$OUTPUT_DIR/WhisperGate_arm64" \
    "$OUTPUT_DIR/WhisperGate_x86_64" \
    -output "$APP_BUNDLE/Contents/MacOS/$APP_NAME"

rm "$OUTPUT_DIR/WhisperGate_arm64" "$OUTPUT_DIR/WhisperGate_x86_64"

echo "Compiled (universal: arm64 + x86_64)."

# Icon
echo "Generating icon..."
bash "$BUILD_DIR/generate_icon.sh"

# Sign
codesign --force --deep --sign - \
    --entitlements "$RESOURCES_DIR/WhisperGate.entitlements" \
    "$APP_BUNDLE"

echo ""
echo "=== Done ==="
echo ""
echo "  open $APP_BUNDLE"
