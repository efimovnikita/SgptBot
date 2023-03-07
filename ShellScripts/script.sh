#!/bin/bash

# Initialize variables with default values
url=""
token=""
prompt="Rewrite this in more simple English words. Use simple structured sentences."

# Parse command line options
while getopts ":u:t:p:" opt; do
  case ${opt} in
    u )
      url=${OPTARG}
      ;;
    t )
      token=${OPTARG}
      ;;
    p )
      prompt=${OPTARG}
      ;;
    \? )
      echo "Invalid option: -$OPTARG" 1>&2
      exit 1
      ;;
    : )
      echo "Option -$OPTARG requires an argument." 1>&2
      exit 1
      ;;
  esac
done

# Shift the positional parameters so that $1 refers to the first non-option argument
shift $((OPTIND -1))

# Download audio
./yt-dlp_linux -x --audio-format mp3 -o input.mp3 "$url" >/dev/null 2>&1

# Split into fragments
mp3splt -t 10.0 input.mp3 >/dev/null 2>&1

rm input.mp3

# Store the file names in an array variable
files=(*.mp3)

# Loop through the array and perform an action on each file
for file in "${files[@]}"
do
    # Extract the base name of the file (i.e., remove the directory path)
    base_name=$(basename "$file")
    # Extract the file name without extension using parameter expansion
    name_without_ext="${base_name%.*}"

    curl --silent https://api.openai.com/v1/audio/translations \
    -X POST \
    -H "Authorization: Bearer $token" \
    -H 'Content-Type: multipart/form-data' \
    -F file=@$file \
    -F model=whisper-1 \
    -F prompt="$prompt" > $name_without_ext.txt \
    -F response_format=text

    # Add a 5 second delay before the next iteration
    sleep 1
done

# Clear
rm *.mp3

# Concatenate all text files in the directory into a single file
cat *.txt > output.txt

cat output.txt

# Clear
rm *.txt
