cmake_minimum_required(VERSION 3.20)
project(RevGameCore LANGUAGES CXX)
add_library(RevGameCore SHARED
    Source/GameCoreExport.cpp
    Source/DuelWorld.cpp
    Source/DuelDamage.cpp
    Source/FixedPoint.cpp
    Source/DeterministicRandom.cpp)
target_compile_features(RevGameCore PRIVATE cxx_std_17)
target_include_directories(RevGameCore PRIVATE Include)
set_property(TARGET RevGameCore PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(MSVC)
    target_compile_options(RevGameCore PRIVATE /W4 /utf-8)
else()
    target_compile_options(RevGameCore PRIVATE -Wall -Wextra -Wpedantic)
endif()
enable_testing()
add_executable(RevGameCoreChecks Tests/PipeChecks.cpp)
target_sources(RevGameCoreChecks PRIVATE Source/FixedPoint.cpp Source/DeterministicRandom.cpp)
target_compile_features(RevGameCoreChecks PRIVATE cxx_std_17)
if(MSVC)
    target_compile_options(RevGameCoreChecks PRIVATE /utf-8)
endif()
target_include_directories(RevGameCoreChecks PRIVATE Include)
target_link_libraries(RevGameCoreChecks PRIVATE RevGameCore)
add_test(NAME DuelPipe COMMAND RevGameCoreChecks)
