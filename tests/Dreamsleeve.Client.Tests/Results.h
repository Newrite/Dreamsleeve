#pragma once

// Results of one kind from a drained output, in the order they were settled.
// The including file imports the module that exports ClientOutput first.
template <class Outcome>
struct Seen
{
  std::uint64_t generation{};
  std::uint64_t requestId{};
  Outcome       value;
};

template <class Outcome>
std::vector<Seen<Outcome>> ResultsOf(const Dreamsleeve::Client::ClientOutput& output)
{
  std::vector<Seen<Outcome>> found;
  for (const auto& result : output.results)
    if (const auto* value = std::get_if<Outcome>(&result.outcome)) found.push_back({result.generation, result.requestId, *value});
  return found;
}
