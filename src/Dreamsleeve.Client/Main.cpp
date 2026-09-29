// Plain translation unit on purpose: it must not include the CommonLib/standard
// headers textually. Main.cpp imports modules that `import std;`, and MSVC 14.51
// rejects the mix of textual <istream> and the std module in one unit (C2079 on
// std::basic_istream::sentry). All CommonLib access lives in module units.
import Dreamsleeve.Plugin;
import Dreamsleeve.ModApi;

namespace SKSE
{

  class LoadInterface;

}

extern "C" __declspec(dllexport) bool SKSEPlugin_Load(const SKSE::LoadInterface* skse)
{
  return Plugin::Load(skse);
}

// Plugin API for other mods, found with GetProcAddress (API/DreamsleeveAPI.h).
// The argument is DreamsleeveAPI::InterfaceVersion, a one-byte enum.
extern "C" __declspec(dllexport) void* RequestPluginAPI(unsigned char version)
{
  return ModApi::Request(version);
}
