module;

#include <enet/enet.h>
#include <magic_enum/magic_enum.hpp>

export module DreamNet.Core;

import std;

import Dreamsleeve.Protocol;

export using BandwidthLimit   = enet_uint32;
export using IpStr            = std::string;
export using IpStrView        = std::string_view;
export using HostName         = std::string;
export using HostNameView     = std::string_view;
export using Port             = std::uint16_t;
export using BandwidthLimit   = enet_uint32;
export using DisconnectReason = Protocol::Network::DisconnectReason;
export using ChannelId        = enet_uint8;
export using ChannelLimit     = size_t;
export using PingIntervalMs   = enet_uint32;
export using TimeOutMs        = enet_uint32;

export enum class DisconnectType : std::uint8_t
{
    Normal,
    Force,
    Later,
};

export enum class DreamNetErrorCode : std::uint16_t
{
    InvalidHost,
    InvalidPeer,
    InvalidPacket,
    FailedHostService,
    FailedEventBuild,
    FailedReceivePacket,
    InvalidAddress,
    InvalidConfig,
    InvalidIp,
    FailedResolveHost,
    FailedFormatAddress,
    FailedCreateServer,
    FailedCreateClient,
    FailedService,
    NullPacket,
    InvalidPacketFlags,
    FailedCreatePacket,
    InvalidPeerState,
    FailedPushPacket,
    InvalidTimeoutConfig,
    InvalidThrottleConfig,
    InvalidPingInterval,
    FailedENetInitialize,
    FailedCreateConnect,
};

export struct DreamNetError final
{
    DreamNetErrorCode code{};
    std::string  message{};
    std::string  operationOverride{};
    std::source_location where = std::source_location::current();
    std::shared_ptr<DreamNetError> cause{};

    static DreamNetError Make(
        const DreamNetErrorCode code,
        std::string message = {},
        std::string operationOverride = {},
        const std::source_location where = std::source_location::current()) noexcept
    {
        return DreamNetError
        {
            .code = code,
            .message = std::move(message),
            .operationOverride = std::move(operationOverride),
            .where = where,
            .cause = nullptr,
        };
    }

    static DreamNetError Wrap(
        const DreamNetErrorCode code,
        DreamNetError inner,
        std::string message = {},
        std::string operationOverride = {},
        const std::source_location where = std::source_location::current())
    {
        return DreamNetError
        {
            .code = code,
            .message = std::move(message),
            .operationOverride = std::move(operationOverride),
            .where = where,
            .cause = std::make_shared<DreamNetError>(std::move(inner)),
        };
    }

    std::string_view CodeName() const noexcept
    {
        const auto name = magic_enum::enum_name(code);
        return name.empty() ? std::string_view{"Unknown"} : name;
    }

    std::string_view Operation() const noexcept
    {
        if (!operationOverride.empty())
        {
            return operationOverride;
        }

        return where.function_name();
    }

    bool HasCause() const noexcept
    {
        return static_cast<bool>(cause);
    }

    const DreamNetError* Cause() const noexcept
    {
        return cause ? cause.get() : nullptr;
    }

    std::string ToLogString() const
    {
        std::string out;
        AppendToLogString(out, *this, 0);
        return out;
    }
    
    static inline std::unexpected<DreamNetError> MakeUnexpected(
        const DreamNetErrorCode code,
        std::string message = {},
        std::string operationOverride = {},
        const std::source_location where = std::source_location::current()) noexcept
    {
        return std::unexpected(
            DreamNetError::Make(
                code,
                std::move(message),
                std::move(operationOverride),
                where));
    }

    static inline std::unexpected<DreamNetError> WrapUnexpected(
        const DreamNetErrorCode code,
        DreamNetError inner,
        std::string message = {},
        std::string operationOverride = {},
        const std::source_location where = std::source_location::current())
    {
        return std::unexpected(
            DreamNetError::Wrap(
                code,
                std::move(inner),
                std::move(message),
                std::move(operationOverride),
                where));
    }

private:
    static void AppendToLogString(
        std::string& out,
        const DreamNetError& error,
        const std::size_t depth)
    {
        out += std::string(depth * 2, ' ');
        out += "code=";
        out += error.CodeName();

        out += ", operation=";
        out += error.Operation();

        if (!error.message.empty())
        {
            out += ", message=";
            out += error.message;
        }

        out += ", file=";
        out += error.where.file_name();
        out += ", line=";
        out += std::to_string(error.where.line());

        out += ", function=";
        out += error.where.function_name();
        out += '\n';

        if (error.cause)
        {
            AppendToLogString(out, *error.cause, depth + 1);
        }
    }
};

export template <typename T>
using NetResult = std::expected<T, DreamNetError>;

export using NetOperationResult = NetResult<std::monostate>;
