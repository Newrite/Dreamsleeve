set_project("Dreamsleeve")
set_version("0.1.0")

add_rules("mode.debug", "mode.releasedbg")
set_defaultplat("windows")
set_defaultarchs("windows|x64")
set_allowedplats("windows")
set_allowedarchs("x64")
set_defaultmode("releasedbg")
set_languages("c++latest")

-- Enable C++20/23 modules for targets that import modules from regular .cpp files.
set_policy("build.c++.modules", true)
-- `build.c++.modules.std` is enabled by default, so `import std;` works without extra config.
set_policy("package.requires_lock", true)

-- Common MSVC flags from Dreamsleeve.Cpp.Common.props
set_warnings("allextra")
add_cxflags("/utf-8", "/external:anglebrackets", "/external:W0", "/external:templates-", {tools = "cl"})
add_syslinks("ws2_32", "winmm")

-- Note:
-- I intentionally do NOT pin set_runtimes("MD")/set_runtimes("MDd") here.
-- If you later decide to mirror a specific vcpkg triplet runtime policy exactly,
-- we can add it back explicitly.

add_requires("enet 1.3.18")
add_requires("spdlog 1.17.0")
add_requires("glaze 7.0.2")
add_requires("doctest 2.5.0")
add_requires("magic_enum 0.9.7")
add_requires("protobuf-cpp 33.2")

local function add_module_interface_files(dir)
    local ixx_files = os.files(path.join(dir, "**.ixx"))
    if #ixx_files > 0 then
        add_files(ixx_files, {public = true})
    end

    local cppm_files = os.files(path.join(dir, "**.cppm"))
    if #cppm_files > 0 then
        add_files(cppm_files, {public = true})
    end
end

local function add_cpp_files(dir)
    local cpp_files = os.files(path.join(dir, "**.cpp"))
    if #cpp_files > 0 then
        add_files(cpp_files)
    end

    local cc_files = os.files(path.join(dir, "**.cc"))
    if #cc_files > 0 then
        add_files(cc_files)
    end

    local cxx_files = os.files(path.join(dir, "**.cxx"))
    if #cxx_files > 0 then
        add_files(cxx_files)
    end
end

local function add_visible_headers(dir)
    add_headerfiles(path.join(dir, "**.h"))
    add_headerfiles(path.join(dir, "**.hpp"))
    add_headerfiles(path.join(dir, "**.hh"))
    add_headerfiles(path.join(dir, "**.hxx"))
end

-- Shared native protocol library
-- Exports its module interface and protobuf runtime to dependents.
target("Dreamsleeve.Protocol.Native")
    set_kind("static")
    set_group("Native")

    add_includedirs("src/Dreamsleeve.Protocol.Native", {public = true})
    add_visible_headers("src/Dreamsleeve.Protocol.Native")
    add_headerfiles("src/Dreamsleeve.Protocol.Native/**.pb.h")
    add_module_interface_files("src/Dreamsleeve.Protocol.Native")
    add_files("src/Dreamsleeve.Protocol.Native/**.pb.cc")

    add_packages("protobuf-cpp", {public = true})

-- Main native core library.
-- This is the module-heavy part of the solution.
target("Dreamsleeve.Client.Core")
    set_kind("static")
    set_group("Native")

    add_includedirs("src/Dreamsleeve.Client.Core", {public = true})
    add_visible_headers("src/Dreamsleeve.Client.Core")
    add_module_interface_files("src/Dreamsleeve.Client.Core")
    add_cpp_files("src/Dreamsleeve.Client.Core")

    add_deps("Dreamsleeve.Protocol.Native")

    add_packages("enet", {public = true})
    add_packages("spdlog", {public = true})
    add_packages("glaze", {public = true})
    add_packages("magic_enum", {public = true})

-- Thin client static library
target("Dreamsleeve.Client")
    set_kind("static")
    set_group("Native")

    add_includedirs("src/Dreamsleeve.Client")
    add_visible_headers("src/Dreamsleeve.Client")
    add_cpp_files("src/Dreamsleeve.Client")

    add_deps("Dreamsleeve.Client.Core")

-- Dev executable
target("Dreamsleeve.Client.Dev")
    set_kind("binary")
    set_group("Apps")

    add_includedirs("src/Dreamsleeve.Client.Dev")
    add_visible_headers("src/Dreamsleeve.Client.Dev")
    add_cpp_files("src/Dreamsleeve.Client.Dev")

    add_deps("Dreamsleeve.Client.Core")

-- Test executable
target("Dreamsleeve.Client.Tests")
    set_kind("binary")
    set_group("Tests")

    add_includedirs("src/Dreamsleeve.Client.Tests")
    add_visible_headers("src/Dreamsleeve.Client.Tests")
    add_cpp_files("src/Dreamsleeve.Client.Tests")

    add_deps("Dreamsleeve.Client.Core")
    add_packages("doctest")
