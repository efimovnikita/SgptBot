FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
EXPOSE 80
EXPOSE 443
COPY bin/Release/net6.0/linux-x64/publish .

# Install Python and pip
RUN apt-get update && apt-get install -y python3-pip

# Install OpenAI Python package
RUN pip3 install openai

ENTRYPOINT ["./ParaphrasmApi"]