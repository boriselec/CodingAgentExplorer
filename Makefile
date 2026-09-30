# publish, not build: build leaves wwwroot behind as a manifest that only
# Development resolves, so a Release run would serve a dashboard with no CSS or JS.
run:
	dotnet publish CodingAgentExplorer -c Release -o Published/CodingAgentExplorer
	cd Published/CodingAgentExplorer && ./CodingAgentExplorer

.DEFAULT_GOAL := run
.PHONY: run
