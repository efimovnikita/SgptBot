#!/bin/bash
dotnet tool uninstall -g SubtitlesExtractorAndRewriter
dotnet pack
dotnet tool install --global --add-source ./nupkg SubtitlesExtractorAndRewriter
echo "Tool installed"