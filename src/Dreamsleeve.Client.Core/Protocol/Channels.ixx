export module Dreamsleeve.Client.ProtocolChannels;
import std;
export import Dreamsleeve.Protocol;

export namespace Dreamsleeve::Client::Wire
{

  using Channel                      = ::Protocol::Network::DeliveryLane;
  constexpr std::size_t ChannelCount = static_cast<std::size_t>(Channel::Poses) + 1;

}
