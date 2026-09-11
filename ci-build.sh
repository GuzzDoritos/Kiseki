#!/usr/bin/env bash
set -e

DOTNET_VERSION=10.0
curl -sSL https://dot.net/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh -c $DOTNET_VERSION -InstallDir ./dotnet

# Export to PATH so 'dotnet' can be called
export DOTNET_ROOT="$PWD/dotnet"
export PATH="$DOTNET_ROOT:$PATH"

dotnet --version

# Publish Kiseki.Web into Release output folder
dotnet publish Kiseki.Web/Kiseki.Web.csproj -c Release -o out
