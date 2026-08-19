#!/bin/sh
set -eu

dotnet package list --project word-mcp.sln --vulnerable --include-transitive --no-restore
dotnet package list --project word-mcp.sln --deprecated --include-transitive --no-restore
