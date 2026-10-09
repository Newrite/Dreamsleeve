export module Dreamsleeve.Host.PhantomSettings;
import std;
import Dreamsleeve.Host.UiSettings;
import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Host
{

  Client::Phantom::ViewSettings PhantomSettings(const UiSettings& ui)
  {
    Client::Phantom::ViewSettings out;
    out.publish                = ui.publishPhantoms;
    out.receive                = ui.showPhantoms;
    out.fallback               = ui.phantomFallback;
    out.hideInCombat           = ui.combatHidePhantoms;

    out.maximum                = static_cast<std::uint32_t>(ui.maxVisiblePhantoms);
    out.distance               = static_cast<float>(ui.phantomDrawDistance);
    out.opacity                = static_cast<float>(ui.phantomOpacity);
    const auto color           = ParseColor(ui.phantomColor).value_or(0x8CCCCC);
    out.color                  = {((color >> 16) & 255) / 255.0f, ((color >> 8) & 255) / 255.0f, (color & 255) / 255.0f};

    out.sampleRate             = static_cast<std::uint32_t>(ui.phantomSampleRate);
    out.delayMs                = static_cast<std::uint32_t>(ui.phantomDelayMs);
    out.extrapolationMs        = static_cast<std::uint32_t>(ui.phantomExtrapolationMs);
    out.timeoutMs              = static_cast<std::uint32_t>(ui.phantomTimeoutMs);

    out.memoryBytes            = static_cast<std::uint64_t>(ui.phantomMemoryMiB) * 1024 * 1024;
    out.diskBytes              = static_cast<std::uint64_t>(ui.phantomCacheMiB) * 1024 * 1024;
    out.uploadBytesPerSecond   = static_cast<std::uint32_t>(ui.phantomUploadKiB) * 1024;
    out.downloadBytesPerSecond = static_cast<std::uint32_t>(ui.phantomDownloadKiB) * 1024;

    return out;
  }

}
