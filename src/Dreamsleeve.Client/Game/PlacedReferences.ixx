module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.PlacedReferences;

import std;

// Temporary placed references owned by this plugin: fireflies and ground
// marks. Between frames a reference is known only by its ObjectRefHandle;
// the handle is resolved in the frame that uses it and the NiPointer lives
// for that call. References are marked temporary at creation so they never
// enter a save game, and they are removed on every context change.
namespace PlacedReferences
{

  // kDataLoaded: the base form is resolved once. A missing form or another
  // record type disables the consumer with an error in the log; there is no
  // hidden fallback.
  export RE::TESObjectSTAT* ResolveStatic(std::string_view label, const std::string& plugin, std::uint32_t formId)
  {
    auto* data   = RE::TESDataHandler::GetSingleton();
    auto* result = data ? data->LookupForm<RE::TESObjectSTAT>(formId, plugin) : nullptr;
    if (!result) logger::error("{} STAT {:X} in {} not found; {} disabled", label, formId, plugin, label);
    return result;
  }

  // SetPosition never re-parents a reference: it stays in the cell it was
  // created in. Once that cell detaches its 3D unloads while the handle stays
  // valid, so the visual has to be recreated in the player's current cell.
  export bool CellAttached(RE::TESObjectREFR& ref)
  {
    auto* cell = ref.GetParentCell();
    return cell && cell->IsAttached();
  }

  export void Remove(std::string_view label, RE::ObjectRefHandle handle)
  {
    if (auto ref = handle.get())
    {
      logger::info("Removing {} reference {:08X}", label, ref->GetFormID());
      // Hide immediately even if engine detachment is deferred.
      if (auto* node = ref->Get3D()) node->SetAppCulled(true);
      ref->Disable();
      ref->SetDelete(true);
    }
  }

  // A new reference in the player's current cell, temporary from creation.
  // The rotation is Euler angles in radians as TESObjectREFR keeps them.
  export std::optional<RE::ObjectRefHandle> Spawn(
    std::string_view    label,
    RE::TESBoundObject* base,
    const RE::NiPoint3& position,
    const RE::NiPoint3& rotation,
    float               scale)
  {
    auto* data   = RE::TESDataHandler::GetSingleton();
    auto* player = RE::PlayerCharacter::GetSingleton();
    auto* cell   = player ? player->GetParentCell() : nullptr;
    if (!data || !cell || !base) return std::nullopt;
    auto* world = cell->IsExteriorCell() ? player->GetWorldspace() : nullptr;
    auto  handle =
      data->CreateReferenceAtLocation(base, position, rotation, cell, world, nullptr, nullptr, RE::ObjectRefHandle{}, false, true);
    auto ref = handle.get();
    if (!ref) return std::nullopt;
    // Dynamic FormID alone does not exclude a reference from saved changes.
    ref->SetTemporary();
    ref->SetScale(scale);
    logger::info("Spawned {} reference {:08X}", label, ref->GetFormID());
    return handle;
  }

  // One reference per key. The consumer decides what is visible each frame;
  // the set keeps handles, recreates detached references and removes the rest.
  export template <class Key>
  class Set final
  {
public:

    explicit Set(std::string_view label) : label{label} {}

    std::size_t Count() const noexcept
    {
      return refs.size();
    }

    // The live reference of a key, or none when it never existed or its cell
    // detached; a detached one is removed so the caller respawns it.
    RE::NiPointer<RE::TESObjectREFR> Resolve(const Key& key)
    {
      const auto found = refs.find(key);
      if (found == refs.end()) return {};
      auto ref = found->second.get();
      if (ref && CellAttached(*ref)) return ref;
      Remove(label, found->second);
      refs.erase(found);
      return {};
    }

    void Keep(const Key& key, RE::ObjectRefHandle handle)
    {
      refs.insert_or_assign(key, handle);
    }

    // Removes everything the frame did not keep.
    template <class Visible>
    void Retain(const Visible& visible)
    {
      std::erase_if(refs, [&](auto& entry) {
        if (visible.contains(entry.first)) return false;
        Remove(label, entry.second);
        return true;
      });
    }

    void Clear()
    {
      for (auto& [key, handle] : refs)
        Remove(label, handle);
      refs.clear();
    }

private:

    std::string                                  label;
    std::unordered_map<Key, RE::ObjectRefHandle> refs;
  };

}
