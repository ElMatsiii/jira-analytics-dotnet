#!/usr/bin/env bash
# ==============================================================================
# MS3 (jira-analytics-dotnet): Coverage Threshold Verification Script
# Parses coverage/coverage.cobertura.xml and asserts >= 60.0%
# across line and branch metrics.
# ==============================================================================
set -euo pipefail

COVERAGE_FILE="${1:-coverage/coverage.cobertura.xml}"
REQUIRED_THRESHOLD="${2:-60.0}"

if [ ! -f "$COVERAGE_FILE" ]; then
  echo "::error::Coverage file '$COVERAGE_FILE' does not exist!"
  echo "Ensure 'dotnet test' with /p:CollectCoverage=true ran prior to this step."
  exit 1
fi

echo "============================================================"
echo "   MS3 (jira-analytics-dotnet) Coverlet Coverage Gate       "
echo "============================================================"
echo "Profile File       : $COVERAGE_FILE"
echo "Required Threshold : ${REQUIRED_THRESHOLD}%"
echo "------------------------------------------------------------"

if command -v python3 &> /dev/null; then
  python3 -c "
import sys, xml.etree.ElementTree as ET

xml_file = sys.argv[1]
threshold = float(sys.argv[2])

try:
    tree = ET.parse(xml_file)
    root = tree.getroot()
except Exception as e:
    print(f'::error::Failed to parse XML from {xml_file}: {e}')
    sys.exit(1)

line_rate = float(root.attrib.get('line-rate', 0.0)) * 100.0
branch_rate = float(root.attrib.get('branch-rate', 0.0)) * 100.0
lines_covered = root.attrib.get('lines-covered', '?')
lines_valid = root.attrib.get('lines-valid', '?')
branches_covered = root.attrib.get('branches-covered', '?')
branches_valid = root.attrib.get('branches-valid', '?')

pass_line = line_rate >= threshold
pass_branch = branch_rate >= threshold

status_line = 'PASS' if pass_line else 'FAIL'
status_branch = 'PASS' if pass_branch else 'FAIL'

print(f'Line Coverage   : {line_rate:6.2f}% ({lines_covered}/{lines_valid})  [{status_line}]')
print(f'Branch Coverage : {branch_rate:6.2f}% ({branches_covered}/{branches_valid})  [{status_branch}]')
print('------------------------------------------------------------')

if not (pass_line and pass_branch):
    print(f'FAILURE: Coverage is below required {threshold:.1f}% threshold!')
    sys.exit(1)
else:
    print(f'SUCCESS: All coverage metrics satisfy >= {threshold:.1f}% threshold!')
    sys.exit(0)
" "$COVERAGE_FILE" "$REQUIRED_THRESHOLD"
else
  # Native awk fallback when python3 is not installed
  LINE_RATE_RAW=$(grep -o 'line-rate="[^"]*"' "$COVERAGE_FILE" | head -n1 | cut -d'"' -f2)
  BRANCH_RATE_RAW=$(grep -o 'branch-rate="[^"]*"' "$COVERAGE_FILE" | head -n1 | cut -d'"' -f2)
  LINES_COVERED=$(grep -o 'lines-covered="[^"]*"' "$COVERAGE_FILE" | head -n1 | cut -d'"' -f2)
  LINES_VALID=$(grep -o 'lines-valid="[^"]*"' "$COVERAGE_FILE" | head -n1 | cut -d'"' -f2)
  BRANCHES_COVERED=$(grep -o 'branches-covered="[^"]*"' "$COVERAGE_FILE" | head -n1 | cut -d'"' -f2)
  BRANCHES_VALID=$(grep -o 'branches-valid="[^"]*"' "$COVERAGE_FILE" | head -n1 | cut -d'"' -f2)

  LINE_RATE=$(awk "BEGIN { printf \"%.2f\", $LINE_RATE_RAW * 100 }")
  BRANCH_RATE=$(awk "BEGIN { printf \"%.2f\", $BRANCH_RATE_RAW * 100 }")

  PASS_LINE=$(awk "BEGIN { print ($LINE_RATE >= $REQUIRED_THRESHOLD) ? 1 : 0 }")
  PASS_BRANCH=$(awk "BEGIN { print ($BRANCH_RATE >= $REQUIRED_THRESHOLD) ? 1 : 0 }")

  STATUS_LINE="FAIL"
  [ "$PASS_LINE" -eq 1 ] && STATUS_LINE="PASS"

  STATUS_BRANCH="FAIL"
  [ "$PASS_BRANCH" -eq 1 ] && STATUS_BRANCH="PASS"

  printf "Line Coverage   : %6.2f%% (%s/%s)  [%s]\n" "$LINE_RATE" "$LINES_COVERED" "$LINES_VALID" "$STATUS_LINE"
  printf "Branch Coverage : %6.2f%% (%s/%s)  [%s]\n" "$BRANCH_RATE" "$BRANCHES_COVERED" "$BRANCHES_VALID" "$STATUS_BRANCH"
  echo "------------------------------------------------------------"

  if [ "$PASS_LINE" -ne 1 ] || [ "$PASS_BRANCH" -ne 1 ]; then
    echo "FAILURE: Coverage is below required ${REQUIRED_THRESHOLD}% threshold!"
    exit 1
  else
    echo "SUCCESS: All coverage metrics satisfy >= ${REQUIRED_THRESHOLD}% threshold!"
    exit 0
  fi
fi
