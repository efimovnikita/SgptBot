FROM ubuntu:latest
WORKDIR /app
COPY bin/Release/net6.0/linux-x64/publish .
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
RUN apt-get update && apt-get -y install python3-pip
RUN pip install shell-gpt --user
ARG API_KEY
RUN mkdir -p /root/.config/shell-gpt && echo "$API_KEY" > /root/.config/shell-gpt/api_key.txt
ENTRYPOINT ["./SgptBot"]
