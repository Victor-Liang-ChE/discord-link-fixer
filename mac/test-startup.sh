#!/bin/bash
set -euo pipefail
task_root=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$task_root/mac/build/tests"
swiftc "$task_root/mac/Startup.swift" "$task_root/mac/verify-startup.swift" -o "$task_root/mac/build/tests/verify-startup"
"$task_root/mac/build/tests/verify-startup"
