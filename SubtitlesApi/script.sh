#!/bin/bash
set -e

DEFAULT_PROMPT="Turn this into the normal text and translate it to simple English. Use simple words. Use simple structured sentences. Provide only translation as result"

while getopts "v:l:p:" opt; do
  case $opt in
    v) video="$OPTARG"
    ;;
    l) language="$OPTARG"
    ;;
    p) prompt="$OPTARG"
    ;;
    \?) echo "Invalid option -$OPTARG" >&2
    ;;
  esac
done

# Set default prompt value if not provided by user
prompt=${prompt:-$DEFAULT_PROMPT}

# Download subtitles
/app/yt-dlp_linux --write-auto-sub --sub-lang $language --skip-download -o "/app/original" $video >/dev/null 2>&1 || { echo "Failed to download subtitles"; exit 1; }

# Process subtitles
sed -e '1,4d' -E -e '/^$|]|>$|%$/d' "/app/original.$language.vtt" | awk '!seen[$0]++' > "/app/processed.txt" || { echo "Failed to process subtitles"; exit 1; }

# Convert subtitles to simple English
/app/SubtitlesSplitter -i "/app/processed.txt" -o "/app/result.txt" --prompt "$prompt" --size 3000 >/dev/null 2>&1 || { echo "Failed to convert subtitles"; exit 1; }

# Print the path to the result file
echo "/app/result.txt"
