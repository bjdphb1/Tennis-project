#!/bin/bash

echo "🛑 Stopping application..."
pkill -f "dotnet run" || echo "No running dotnet process found"
pkill -f "MyConsoleApp" || echo "No MyConsoleApp process found"
sleep 2

echo ""
echo "🧹 Clearing duplicate tracking files..."
rm -f placed_bets.json blocked_matches.json
rm -f output_1.csv output_2.csv thread_1.csv thread_2.csv
echo "✓ Cleared: placed_bets.json, blocked_matches.json, CSV files"

echo ""
echo "🔨 Rebuilding predictors with $1 stake..."
cd Predictors/Predictor1
dotnet clean > /dev/null 2>&1
dotnet publish -c Release -r osx-arm64 --self-contained > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Predictor1 published to Release"
else
    echo "❌ Predictor1 publish failed"
    exit 1
fi

cd ../Predictor2
dotnet clean > /dev/null 2>&1
dotnet publish -c Release -r osx-arm64 --self-contained > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Predictor2 published to Release"
else
    echo "❌ Predictor2 publish failed"
    exit 1
fi

cd ../..

echo ""
echo "🧪 Testing predictors..."

# Create test input
echo "Header line (will be skipped)" > test_input.csv
echo "0,Test Player 1,Test Player 2,1.5,2.5,100,200,50,50,50,50" >> test_input.csv

# Test Predictor1
./Predictors/Predictor1/bin/Release/net8.0/osx-arm64/publish/Predictor1 test_input.csv test_output_1.csv > /dev/null 2>&1
if [ -f test_output_1.csv ]; then
    stake=$(grep -v "^0," test_output_1.csv | head -1 | cut -d',' -f3)
    if [ "$stake" == "1" ]; then
        echo "✓ Predictor1 verified: Stake=$1"
    else
        echo "❌ Predictor1 ERROR: Stake=$stake (expected 1)"
        rm -f test_input.csv test_output_1.csv test_output_2.csv
        exit 1
    fi
else
    echo "⚠ Predictor1: No output (OK if no valid input)"
fi

# Test Predictor2
./Predictors/Predictor2/bin/Release/net8.0/osx-arm64/publish/Predictor2 test_input.csv test_output_2.csv > /dev/null 2>&1
if [ -f test_output_2.csv ]; then
    stake=$(grep -v "^0," test_output_2.csv | head -1 | cut -d',' -f3)
    if [ "$stake" == "1" ]; then
        echo "✓ Predictor2 verified: Stake=$1"
    else
        echo "❌ Predictor2 ERROR: Stake=$stake (expected 1)"
        rm -f test_input.csv test_output_1.csv test_output_2.csv
        exit 1
    fi
else
    echo "⚠ Predictor2: No output (OK if no valid input)"
fi

# Cleanup test files
rm -f test_input.csv test_output_1.csv test_output_2.csv

echo ""
echo "✅ Fix Complete!"
echo ""
echo "📋 Next Steps:"
echo "   1. Run: dotnet run"
echo "   2. Log in to Dexsport when browser opens"
echo "   3. Watch for 'Stake=1' in output"
echo ""
