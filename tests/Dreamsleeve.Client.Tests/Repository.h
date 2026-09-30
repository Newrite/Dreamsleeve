#pragma once

// Files of the repository, found from the test's working directory.
// The including file includes doctest and imports std first.
inline std::filesystem::path RepositoryRoot()
{
  for (auto directory = std::filesystem::current_path(); !directory.empty(); directory = directory.parent_path())
  {
    if (std::filesystem::exists(directory / "xmake.lua")) return directory;
    if (directory == directory.parent_path()) break;
  }
  FAIL("xmake.lua not found above the working directory");
  return {};
}

// The text with line endings normalized to LF.
inline std::string ReadText(const std::filesystem::path& path)
{
  std::ifstream input{path, std::ios::binary};
  std::string   text{std::istreambuf_iterator<char>{input}, {}};
  std::erase(text, '\r');
  return text;
}
