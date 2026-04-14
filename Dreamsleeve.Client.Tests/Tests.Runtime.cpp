#include <doctest/doctest.h>
#include <enet/enet.h>
import DreamNet.Runtime;
import std;

TEST_CASE("DreamNetRuntime.TryInitialize - success", "[runtime]")
{
    auto result = DreamNetRuntime::TryInitialize();
    REQUIRE(result.has_value());

    auto runtime = std::move(result.value());
    // runtime deinitializes on destruction at end of scope
}

TEST_CASE("DreamNetRuntime move constructor transfers ownership", "[runtime]")
{
    auto result = DreamNetRuntime::TryInitialize();
    REQUIRE(result.has_value());

    auto runtime1 = std::move(result.value());
    auto runtime2 = std::move(runtime1);

    // After move, runtime1 no longer owns the runtime (won't deinitialize)
    // runtime2 owns it and will deinitialize on destruction
}

TEST_CASE("DreamNetRuntime move assignment transfers ownership", "[runtime]")
{
    auto result1 = DreamNetRuntime::TryInitialize();
    auto result2 = DreamNetRuntime::TryInitialize();
    REQUIRE(result1.has_value());
    REQUIRE(result2.has_value());

    auto runtime1 = std::move(result1.value());
    auto runtime2 = std::move(result2.value());

    // runtime1 deinitializes first
    runtime1 = std::move(runtime2);
    // Now runtime1 owns the second runtime, the first was already released
}

TEST_CASE("DreamNetRuntime scope-based lifetime", "[runtime]")
{
    // This test verifies that the runtime can be created and destroyed cleanly
    {
        auto result = DreamNetRuntime::TryInitialize();
        REQUIRE(result.has_value());
        auto runtime = std::move(result.value());
    }
    // runtime is destroyed here, ENet should be deinitialized

    // Re-initialize in a new scope to verify clean shutdown
    {
        auto result = DreamNetRuntime::TryInitialize();
        REQUIRE(result.has_value());
        auto runtime = std::move(result.value());
    }
}
