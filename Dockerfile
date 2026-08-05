# syntax=docker/dockerfile:1.4

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:alpine AS frontend-build

WORKDIR /frontend
COPY ./frontend/package.json ./frontend/package-lock.json ./

RUN --mount=type=cache,target=/root/.npm \
    npm ci

COPY ./frontend ./

RUN npm run build
RUN npm run build:server
RUN --mount=type=cache,target=/root/.npm \
    npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build

WORKDIR /backend

# Accept build-time architecture as ARG (e.g., x64 or arm64)
ARG TARGETARCH
COPY ./backend/NzbWebDAV.csproj ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore -r linux-musl-${TARGETARCH}

COPY ./backend ./

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish -c Release -r linux-musl-${TARGETARCH} \
        --no-restore -o ./publish

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# Label the image
ARG REPO_URL
LABEL org.opencontainers.image.source=${REPO_URL}

# Prepare environment
WORKDIR /app
RUN mkdir /config \
    && apk add --no-cache nodejs npm libc6-compat shadow su-exec bash curl tzdata

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
COPY version.txt /app/version.txt
RUN chmod +x /entrypoint.sh

# Set env variables
EXPOSE 3000
# Optional build-arg; entrypoint.sh falls back to /app/version.txt
# when this is empty so Coolify-style deploys (no build-arg) still
# show the right version in the UI.
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

CMD ["/entrypoint.sh"]
