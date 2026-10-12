# syntax=docker/dockerfile:1
#
# Build from the repository root:   docker build -t caseregister-api .
# Render builds this file as it is. Nothing secret is in the image: every setting arrives as an
# environment variable when the container starts.

# ---- Build: restore, then publish. The SDK image is not part of what ships. ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so the restore layer is reused until a package changes.
COPY .editorconfig Directory.Build.props Directory.Packages.props ./
COPY CaseRegister.Domain/CaseRegister.Domain.csproj CaseRegister.Domain/
COPY CaseRegister.Application/CaseRegister.Application.csproj CaseRegister.Application/
COPY CaseRegister.Infrastructure/CaseRegister.Infrastructure.csproj CaseRegister.Infrastructure/
COPY CaseRegister.Api/CaseRegister.Api.csproj CaseRegister.Api/
RUN dotnet restore CaseRegister.Api/CaseRegister.Api.csproj

COPY CaseRegister.Domain/ CaseRegister.Domain/
COPY CaseRegister.Application/ CaseRegister.Application/
COPY CaseRegister.Infrastructure/ CaseRegister.Infrastructure/
COPY CaseRegister.Api/ CaseRegister.Api/
RUN dotnet publish CaseRegister.Api/CaseRegister.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# ---- Run: the chiseled ASP.NET Core runtime. No shell, no package manager, no root. ----
# Chiseled has no ICU or time zone data. The app needs neither: it formats with the invariant culture and
# works out Zambian time as UTC+2. (The -extra image would add them if that ever changes.)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production \
    # The runtime's diagnostics channel creates a socket file. Off, so the app never needs to write to its own filesystem.
    DOTNET_EnableDiagnostics=0

# Not root: the image's own unprivileged user.
USER $APP_UID

# The app listens on $PORT when the platform sets it (Render does), and on 8080 otherwise.
EXPOSE 8080

# The runtime image has no curl or wget, so the app checks itself: it asks its own /health/live and exits 0 or 1.
# /health/live checks nothing else, so this never wakes the database.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "CaseRegister.Api.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "CaseRegister.Api.dll"]
