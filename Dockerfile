# Образ с ядром архиватора (консольная программа archiver).
# Когда появится сервис с API, он добавится сюда же, и этот образ поедет на сервер.
#
# Собрать:    docker build -t archiver .
# Запустить:  docker run --rm -v "$PWD:/data" archiver compress /data/input.bin /data/output.arc

# ---------- Этап 1: сборка ----------
# Образ с .NET SDK: в нём есть компилятор, нужен только для сборки
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем только ядро: остальное для сборки не нужно
COPY core-cs/ .
RUN dotnet publish Archiver.Cli -c Release -o /out

# ---------- Этап 2: запуск ----------
# Лёгкий образ только со средой выполнения .NET, без компилятора
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .

# Запускаем не от root, а от обычного пользователя app, который уже есть в образе Microsoft
USER $APP_UID

ENTRYPOINT ["./archiver"]
