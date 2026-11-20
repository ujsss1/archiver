# Образ с HTTP-сервисом архиватора (API арены + веб-страница). Ядро собирается внутри.
# Именно этот образ запускается на сервере.
#
# Собрать:    docker build -t archiver-service .
# Запустить:  docker run --rm -p 8080:8080 archiver-service
# На сервере удобнее через docker compose (см. docker-compose.yml)

# ---------- Этап 1: сборка ----------
# Образ с .NET SDK: в нём есть компилятор, нужен только для сборки
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем только core-cs: там ядро и сервис, остальное для сборки не нужно
COPY core-cs/ ./core-cs/

# Обязательно Release: в Debug ядро работает в разы медленнее, и большие файлы уходят без сжатия
RUN dotnet publish core-cs/archiver-service.cs -c Release -o /app

# ---------- Этап 2: запуск ----------
# Образ со средой выполнения ASP.NET, без компилятора
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Порт, который слушает сервис внутри контейнера
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Запускаем не от root, а от обычного пользователя app, который уже есть в образе Microsoft
USER $APP_UID

ENTRYPOINT ["dotnet", "archiver-service.dll"]
