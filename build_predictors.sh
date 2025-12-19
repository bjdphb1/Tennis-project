#!/bin/bash

echo "🚀 Building cross-platform predictors (Windows, macOS, Linux)..."
echo ""

# Build Predictor1
echo "📦 Building Predictor1..."
dotnet publish Predictors/Predictor1/Predictor1.csproj \
    -c Release \
    -f net8.0 \
    --no-self-contained \
    -o Predictors/Predictor1/bin/Release/net8.0/publish

if [ $? -eq 0 ]; then
    echo "✅ Predictor1 built successfully"
else
    echo "❌ Predictor1 build failed"
    exit 1
fi

echo ""

# Build Predictor2
echo "📦 Building Predictor2..."
dotnet publish Predictors/Predictor2/Predictor2.csproj \
    -c Release \
    -f net8.0 \
    --no-self-contained \
    -o Predictors/Predictor2/bin/Release/net8.0/publish

if [ $? -eq 0 ]; then
    echo "✅ Predictor2 built successfully"
else
    echo "❌ Predictor2 build failed"
    exit 1
fi

echo ""
echo "🎉 All predictors built successfully!"
echo ""
echo "📂 Published locations:"
echo "  - Predictor1: Predictors/Predictor1/bin/Release/net8.0/publish/Predictor1.dll"
echo "  - Predictor2: Predictors/Predictor2/bin/Release/net8.0/publish/Predictor2.dll"
echo ""
echo "💡 These builds work on Windows, macOS, and Linux!"
echo "💡 To run manually: dotnet Predictor1.dll <input.csv> <output.csv>"
echo ""
