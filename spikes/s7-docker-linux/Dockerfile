FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    gcc libc6-dev linux-libc-dev weston wl-clipboard ydotool procps iproute2 \
    && rm -rf /var/lib/apt/lists/*
