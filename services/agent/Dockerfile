# Сборка: компилятор, CMake, libcurl, gRPC и protobuf для генерации кода из proto/
FROM ubuntu:24.04 AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        g++ cmake make \
        libcurl4-openssl-dev \
        libgrpc++-dev libgrpc-dev \
        libprotobuf-dev protobuf-compiler protobuf-compiler-grpc \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Копируем proto из корня репозитория и исходники агента
COPY proto/ proto/
COPY services/agent/CMakeLists.txt services/agent/
COPY services/agent/include/ services/agent/include/
COPY services/agent/src/ services/agent/src/

WORKDIR /src/services/agent

RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF \
    && cmake --build build -j

# Запуск: только бинарник и рантайм-библиотеки
FROM ubuntu:24.04
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libcurl4t64 \
        libgrpc++1.51t64 libgrpc29t64 \
        libprotobuf32t64 \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /src/services/agent/build/load_agent /usr/local/bin/load_agent

USER nobody
ENTRYPOINT ["load_agent"]