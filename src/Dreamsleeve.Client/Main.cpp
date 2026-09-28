// Plain translation unit on purpose: it must not include the CommonLib/standard
// headers textually. Main.cpp imports modules that `import std;`, and MSVC 14.51
// rejects the mix of textual <istream> and the std module in one unit (C2079 on
// std::basic_istream::sentry). All CommonLib access lives in module units.
import Dreamsleeve.Plugin;

namespace SKSE
{

  class LoadInterface;

}

extern "C" __declspec(dllexport) bool SKSEPlugin_Load(const SKSE::LoadInterface* skse)
{
  return Plugin::Load(skse);
}
