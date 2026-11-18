# The full application in one Linux image: React is built and served by the ASP.NET Core server.
# Used by compose.yaml (docker compose up --build). Nothing is compiled on the Windows host,
# so Smart App Control is not involved: the code runs inside the Linux VM of Docker Desktop.

# 1. React → static files
FROM node:24-alpine AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
# vite.config.ts writes the output to ../backend/Attendance.Api/wwwroot
RUN npm run build

# 2. ASP.NET Core server (the tests are not part of the image; they run in CI)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY global.json ./
COPY backend/Attendance.Api/Attendance.Api.csproj backend/Attendance.Api/
RUN dotnet restore backend/Attendance.Api/Attendance.Api.csproj
COPY backend/Attendance.Api/ backend/Attendance.Api/
COPY --from=web /src/backend/Attendance.Api/wwwroot backend/Attendance.Api/wwwroot
RUN dotnet publish backend/Attendance.Api/Attendance.Api.csproj -c Release -o /app --no-restore

# 3. Runtime only
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
# Overrides "Urls" from appsettings.json: inside the container the server must listen on all interfaces.
ENV Urls=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Attendance.Api.dll"]
