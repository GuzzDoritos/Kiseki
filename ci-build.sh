#!/usr/bin/env bash
set -e

DOTNET_VERSION=10.0
curl -sSL https://dot.net/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh -c $DOTNET_VERSION -InstallDir ./dotnet
./dotnet/dotnet --version
