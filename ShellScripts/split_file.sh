#!/bin/bash

if [ $# -lt 1 ]; then
  echo "Usage: $0 input_file_path"
  exit 1
fi

input_file_path="$1"

tr -d '\n' < "$input_file_path" | awk -v chunk_size=2000 '{
  text=$0
  while (length(text) > 0) {
    # Split text on the last whitespace character before the chunk size
    if (length(text) > chunk_size) {
      split_pos = match(substr(text, 1, chunk_size), /\.[^\.]*$/)
      if (split_pos > 0) {
        chunk = substr(text, 1, split_pos)
        text = substr(text, split_pos+1)
      } else {
        chunk = substr(text, 1, chunk_size)
        text = substr(text, chunk_size+1)
      }
    } else {
      chunk = text
      text = ""
    }
    print chunk "\n\n"
  }
}'
