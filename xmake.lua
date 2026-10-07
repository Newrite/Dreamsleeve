-- One version for the client and the server: Directory.Build.props and the UI package.json repeat it.
set_version("1.2.0")

add_rules("mode.debug", "mode.releasedbg")
set_defaultplat("windows")
set_defaultarchs("windows|x64")
set_allowedplats("windows")
set_allowedarchs("x64")
set_defaultmode("releasedbg")
set_languages("c++23")

-- Enable C++20/23 modules for targets that import modules from regular .cpp files.
set_policy("build.c++.modules", true)
-- `build.c++.modules.std` is enabled by default, so `import std;` works without extra config.
set_policy("package.requires_lock", true)

-- Common MSVC flags from Dreamsleeve.Cpp.Common.props
set_warnings("allextra")
add_cxflags("/utf-8", "/external:anglebrackets", "/external:W0", "/external:templates-", {tools = "cl"})
add_syslinks("ws2_32", "winmm")

-- includes
includes(os.getenv("CommonLibSSE-NG"))

-- Included projects may set their own name; restore the solution name afterwards.
set_project("Dreamsleeve")

-- utils.bin2c ordered before the module scanner, which already reads the header
-- a module includes; the stock rule is ordered before the builder only.
rule("dreamsleeve.embed")
    set_extensions(".toml")
    add_orders("dreamsleeve.embed", "c++.build.modules.scanner")
    -- Public: dependents compile the module interfaces that include the header.
    on_load(function (target)
        target:add("includedirs", path.join(target:autogendir(), "rules", "utils", "bin2c"), {public = true})
    end)
    on_preparecmd_file(function (target, batchcmds, sourcefile, opt)
        import("rules.utils.bin2c.utils", {alias = "bin2c_utils", rootdir = os.programdir()})
        local headerfile = bin2c_utils.generate_headerfile(target, batchcmds, sourcefile, {progress = opt.progress, zeroend = false})
        batchcmds:add_depfiles(sourcefile)
        batchcmds:set_depmtime(os.mtime(headerfile))
        batchcmds:set_depcache(target:dependfile(headerfile))
    end)
rule_end()

add_requires("enet 1.3.18")
-- Keep CommonLib and Core on the same compiled spdlog configuration.
add_requires("spdlog 1.17.0", {configs = {header_only = false, wchar = true, std_format = true}})
add_requires("glaze 7.0.2")
add_requires("doctest 2.5.0")
add_requires("magic_enum 0.9.7")
add_requires("protobuf-cpp 33.2")

set_config("skyrim_vr", true)
set_config("skyrim_ae", true)
set_config("skyrim_se", true)
set_config("skse_xbyak", true)

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
    -- Picks up the generated **.pb.cc plus plain .cpp units such as
    -- ProtocolContract.cpp, which may include network.pb.h (a module interface
    -- may not - see the comment in Dreamsleeve.Protocol.Native.ixx).
    add_cpp_files("src/Dreamsleeve.Protocol.Native")

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
    add_syslinks("winhttp", "advapi32", "bcrypt", "ole32", "oleaut32", "uuid", "user32", {public = true})
    -- The client's version, sent as its HTTP User-Agent. Public like the
    -- embed header: dependents compile the module interface that uses it.
    on_load(function (target)
        target:add("defines", "DREAMSLEEVE_VERSION=\"" .. (target:version() or "0.0.0") .. "\"", {public = true})
    end)
    -- The documented client.toml doubles as the file written on first run.
    add_rules("dreamsleeve.embed")
    add_files("src/Dreamsleeve.Client.Core/client.example.toml")

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
    add_module_interface_files("src/Dreamsleeve.Client")
    add_cpp_files("src/Dreamsleeve.Client")

    add_deps("Dreamsleeve.Client.Core")
    add_deps("commonlibsse-ng")

    add_rules("commonlibsse-ng.plugin", {
        name = "DreamsleeveClient",
        author = "Newrite",
        description = "Skyrim online chat and players presence system."
    })

-- Dev executable
target("Dreamsleeve.Client.Dev")
    set_kind("binary")
    set_group("Apps")

    add_includedirs("src/Dreamsleeve.Client.Dev")
    add_visible_headers("src/Dreamsleeve.Client.Dev")
    add_cpp_files("src/Dreamsleeve.Client.Dev")

    add_deps("Dreamsleeve.Client.Core")

-- UI sources for IDE navigation. Vite remains the UI build entry point.
target("Dreamsleeve.Client.UI")
    set_kind("phony")
    set_group("UI")
    set_default(false)

    local ui = "src/Dreamsleeve.Client.UI"
    add_extrafiles(path.join(ui, "*"))
    add_extrafiles(path.join(ui, ".gitignore"))
    for _, dir in ipairs({"src", "public", "scripts", "tests"}) do
        add_extrafiles(path.join(ui, dir, "**"))
    end

-- Test executable
target("Dreamsleeve.Client.Tests")
    set_kind("binary")
    set_group("Tests")

    add_includedirs("tests/Dreamsleeve.Client.Tests")
    add_visible_headers("tests/Dreamsleeve.Client.Tests")
    add_cpp_files("tests/Dreamsleeve.Client.Tests")
    -- Game-independent host modules of the SKSE adapter are compiled here too:
    -- they depend on Core only, so bridge/settings/session logic is tested without Skyrim.
    add_module_interface_files("src/Dreamsleeve.Client/Host")

    add_deps("Dreamsleeve.Client.Core")
    add_packages("doctest")
