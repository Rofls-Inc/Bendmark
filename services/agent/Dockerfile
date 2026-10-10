# Сборка: компилятор, CMake и заголовки libcurl нужны только здесь
FROM ubuntu:24.04 AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends g++ cmake make libcurl4-openssl-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY CMakeLists.txt .
COPY include/ include/
COPY src/ src/
RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build -j

# Запуск: только сам бинарник и библиотека libcurl без компилятора
FROM ubuntu:24.04
RUN apt-get update \
    && apt-get install -y --no-install-recommends libcurl4t64 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/build/load_agent /usr/local/bin/load_agent
USER nobody
ENTRYPOINT ["load_agent"]
