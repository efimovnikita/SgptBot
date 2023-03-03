import argparse
import openai

# Define command line arguments
parser = argparse.ArgumentParser(description='Tool for communicating with GPT-3')
parser.add_argument('--prompt', type=str, help='Prompt for GPT-3.5')
parser.add_argument('--key', type=str, help='API KEY')
parser.add_argument('--model', type=str, default='gpt-3.5-turbo', help='Model for GPT-3')
args = parser.parse_args()

# Validate arguments
if not args.prompt:
    parser.error("Please provide a prompt")

if not args.key:
    parser.error("Please provide an API key")

openai.api_key = args.key

messages = [{"role": "user", "content": args.prompt}]
chat = openai.ChatCompletion.create(model=args.model, messages=messages)
reply: object = chat.choices[0].message.content
print(reply)
