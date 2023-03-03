import argparse
import openai

parser = argparse.ArgumentParser(description='Tool for communicating with GPT-3')
parser.add_argument('--prompt', type=str, help='Prompt for GPT-3.5')
parser.add_argument('--key', type=str, help='API KEY')
args = parser.parse_args()

openai.api_key = args.key

messages = [{"role": "user", "content": args.prompt}]
chat = openai.ChatCompletion.create(model="gpt-3.5-turbo", messages=messages)
reply: object = chat.choices[0].message.content
print(reply)
