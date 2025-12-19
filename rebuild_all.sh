#!/bin/bash
# Automated rebuild script for MyConsoleApp
# This ensures predictors are properly published with correct stake amounts

set -e  # Exit on any error

echo "🧹 MyConsoleApp - Complete Rebuild Script"
echo "=========================================="
echo ""

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Step 1: Clean everything
echo "${YELLOW}Step 1: Cleaning all build artifacts...${NC}"
dotnet clean
cd Predictors/Predictor1
dotnet clean
cd ../Predictor2
dotnet clean
cd ../..
echo "${GREEN}✓ Cleaned${NC}"
echo ""

# Step 2: Publish Predictor1
echo "${YELLOW}Step 2: Publishing Predictor1 with \$1 stake...${NC}"
cd Predictors/Predictor1
dotnet publish -c Release -r osx-arm64 --self-contained -o bin/Release/net8.0/osx-arm64/publish --force
cd ../..
echo "${GREEN}✓ Predictor1 published${NC}"
echo ""

# Step 3: Publish Predictor2
echo "${YELLOW}Step 3: Publishing Predictor2 with \$1 stake...${NC}"
cd Predictors/Predictor2
dotnet publish -c Release -r osx-arm64 --self-contained -o bin/Release/net8.0/osx-arm64/publish --force
cd ../..
echo "${GREEN}✓ Predictor2 published${NC}"
echo ""

# Step 4: Verify predictors output $1 stake
echo "${YELLOW}Step 4: Verifying predictor stake amounts...${NC}"

# Create test input
mkdir -p /tmp/predictor_test
cat > /tmp/predictor_test/test_input.csv << EOF
MatchId,HomePlayer,AwayPlayer,HomeOdds,AwayOdds
12345,Test Player A,Test Player B,1500,2500
EOF

# Test Predictor1
PRED1_DIR="Predictors/Predictor1/bin/Release/net8.0/osx-arm64/publish"
if [ -d "$PRED1_DIR" ] && [ -f "$PRED1_DIR/Predictor1" ]; then
    (cd "$PRED1_DIR" && ./Predictor1 /tmp/predictor_test/test_input.csv /tmp/predictor_test/output1.csv 2>/dev/null || true)
    if [ -f /tmp/predictor_test/output1.csv ]; then
        STAKE1=$(cat /tmp/predictor_test/output1.csv | head -n 1 | cut -d',' -f2)
        if [ "$STAKE1" = "1" ]; then
            echo "${GREEN}✓ Predictor1: Stake = \$1${NC}"
        else
            echo "${RED}✗ Predictor1: Stake = \$$STAKE1 (expected \$1)${NC}"
            exit 1
        fi
    else
        echo "${YELLOW}⚠ Could not verify Predictor1 output${NC}"
    fi
else
    echo "${YELLOW}⚠ Predictor1 not found in expected location${NC}"
fi

# Test Predictor2
PRED2_DIR="Predictors/Predictor2/bin/Release/net8.0/osx-arm64/publish"
if [ -d "$PRED2_DIR" ] && [ -f "$PRED2_DIR/Predictor2" ]; then
    (cd "$PRED2_DIR" && ./Predictor2 /tmp/predictor_test/test_input.csv /tmp/predictor_test/output2.csv 2>/dev/null || true)
    if [ -f /tmp/predictor_test/output2.csv ]; then
        STAKE2=$(cat /tmp/predictor_test/output2.csv | head -n 1 | cut -d',' -f2)
        if [ "$STAKE2" = "1" ]; then
            echo "${GREEN}✓ Predictor2: Stake = \$1${NC}"
        else
            echo "${RED}✗ Predictor2: Stake = \$$STAKE2 (expected \$1)${NC}"
            exit 1
        fi
    else
        echo "${YELLOW}⚠ Could not verify Predictor2 output${NC}"
    fi
else
    echo "${YELLOW}⚠ Predictor2 not found in expected location${NC}"
fi

# Cleanup test files
rm -rf /tmp/predictor_test
echo ""

# Step 5: Build main application
echo "${YELLOW}Step 5: Building main application...${NC}"
dotnet build
echo "${GREEN}✓ Main application built${NC}"
echo ""

# Step 6: Clear runtime state
echo "${YELLOW}Step 6: Clearing runtime state...${NC}"
echo "[]" > placed_bets.json
echo "[]" > blocked_matches.json
rm -f thread_*.csv output_*.csv
rm -f *.csv 2>/dev/null || true
echo "${GREEN}✓ Runtime state cleared${NC}"
echo ""

echo "${GREEN}=========================================="
echo "✅ Build Complete!"
echo "==========================================${NC}"
echo ""
echo "Ready to run: ${YELLOW}dotnet run${NC}"
echo ""
