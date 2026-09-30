export module Dreamsleeve.Host.Commands;

import std;
export import Dreamsleeve.Host.Session;
export import Dreamsleeve.Host.Bubbles;

// UI commands applied to the host: the chat session, ui.toml and the account.
// The SKSE adapter parses a command, hands it here with its ports and sends
// the events back to the page. No game headers: compiled into the tests.
export namespace Dreamsleeve::Host
{

  // Where a ground note would stand now, with the in-game date.
  struct NoteSpot
  {
    Domain::GroundMarkPlacement placement;
    Domain::GameDate            gameDate;
  };

  // The plugin around the host state, as UI commands need it.
  struct CommandPorts
  {
    // Writes ui.toml with the current names book.
    std::function<std::expected<void, std::string>()> saveUi;
    // Leaves chat focus: the page asked to close.
    std::function<void()> close;
    // Applies the chat activation key of saved settings.
    std::function<void(std::string_view)> activationKey;
    // The character's spot in the world; empty outside it.
    std::function<std::optional<NoteSpot>()> noteSpot;
  };

  struct CommandContext
  {
    ClientExchange& exchange;
    Session&        session;
    UiFile&         ui;
    Bubbles&        bubbles;
    // Stops automatic reconnects until an explicit sign-in.
    bool&        manualDisconnect;
    CommandPorts ports;
  };

  // Events for the page, in order, and diagnostics for the host logger.
  struct CommandOutput
  {
    std::vector<Bridge::HostEvent> events;
    std::vector<std::string>       notes;
  };

  namespace Detail
  {

    namespace Commands = Bridge::Commands;

    // One overload per command and no fallback: a new command does not
    // compile until it is handled here.
    struct CommandHandler
    {
      CommandContext& context;
      CommandOutput   output;

      void Emit(Bridge::HostEvent event)
      {
        output.events.push_back(std::move(event));
      }

      void Save()
      {
        if (auto saved = context.ports.saveUi(); !saved) output.notes.push_back(saved.error());
      }

      // Names or the ignore list changed: saved, then every surface is projected again.
      void Reproject()
      {
        Save();
        context.session.Refresh();
      }

      // A local refusal of an account operation, shown with the current auth state.
      void Admitted(const std::expected<void, std::string>& admitted)
      {
        if (admitted) return;
        auto event  = Bridge::AuthState(context.exchange.Status(), context.ui.ui.chat.streamerMode);
        event.error = Bridge::ClipError(admitted.error());
        Emit(std::move(event));
      }

      void operator()(Commands::SendChat& command)
      {
        if (auto sent = context.session.SendChat(context.exchange, command); !sent)
          Emit(Bridge::SendResultEvent{.requestId = command.requestId, .error = sent.error()});
      }

      void operator()(Commands::Close&)
      {
        context.ports.close();
      }

      void operator()(Commands::SaveSettings& command)
      {
        auto&      chat  = context.ui.ui.chat;
        const bool names = InstantChanged(chat, command.settings);
        chat             = std::move(command.settings);
        if (names)
        {
          context.session.Refresh();
          Emit(context.session.IgnoredList(chat));
        }
        context.ports.activationKey(chat.activationKey);
        Bridge::SettingsResultEvent result{.revision = command.revision};
        if (auto saved = context.ports.saveUi(); !saved) result.error = Bridge::ClipError(saved.error());
        Emit(std::move(result));
      }

      void operator()(Commands::SignIn& command)
      {
        // The Core keeps its own copy; the command's is wiped.
        Client::Auth::Credentials credentials{command.username, command.password};
        std::ranges::fill(command.password, '\0');
        context.manualDisconnect = false;
        Admitted(context.exchange.PostLogin(std::move(credentials), std::move(command.displayName), command.remember));
      }

      void operator()(Commands::Ignore& command)
      {
        if (context.session.Ignore(command.playerId.value))
        {
          // A visible bubble goes at once; history is re-projected without it.
          context.bubbles.Erase(command.playerId.value);
          Reproject();
        }
        Emit(context.session.IgnoredList(context.ui.ui.chat));
      }

      void operator()(Commands::Unignore& command)
      {
        if (context.session.Unignore(command.playerId.value)) Reproject();
        Emit(context.session.IgnoredList(context.ui.ui.chat));
      }

      void operator()(Commands::DisplaySettings& command)
      {
        // Applied and saved at once: every surface switches without reconnecting.
        ApplyInstant(context.ui.ui.chat, command.settings);
        // Visible bubbles were admitted under the old filter.
        context.bubbles.Clear();
        Reproject();
        Emit(context.session.IgnoredList(context.ui.ui.chat));
      }

      void operator()(Commands::SignInSaved&)
      {
        context.manualDisconnect = false;
        Admitted(context.exchange.PostAuthentication(Client::ResumeLogin{}));
      }

      void operator()(Commands::SignOut&)
      {
        context.manualDisconnect = true;
        Admitted(context.exchange.PostAuthentication(Client::SignOutAccount{}));
      }

      void operator()(Commands::ForgetLogin&)
      {
        context.manualDisconnect = true;
        Admitted(context.exchange.PostAuthentication(Client::ForgetLogin{}));
      }

      void operator()(Commands::Disconnect&)
      {
        context.manualDisconnect = true;
        context.exchange.RequestDisconnect();
      }

      void operator()(Commands::PlaceGroundNote& command)
      {
        // The note stands where the character stands now; without a ready
        // world there is nowhere to put it.
        std::expected<void, std::string> placed = std::unexpected{"Персонаж не в игровом мире"};
        if (const auto spot = context.ports.noteSpot())
          placed =
            context.session.PlaceGroundNote(context.exchange, command.requestId, std::move(command.text), spot->placement, spot->gameDate);
        if (!placed) Emit(Bridge::MarkResultEvent{.requestId = command.requestId, .error = placed.error()});
      }

      void operator()(Commands::RemoveGroundMark& command)
      {
        if (auto removed = context.session.RemoveGroundMark(context.exchange, command.requestId, command.markId.value); !removed)
          Emit(Bridge::MarkResultEvent{.requestId = command.requestId, .error = removed.error()});
      }

      void operator()(Commands::SetIdentityVisibility& command)
      {
        // A ready session asks the server; with no session only the choice for
        // the next one changes. While a session is being opened it waits.
        auto&      session = context.session;
        const auto hiding  = Bridge::HidingOf(command.hiding);
        if (session.Ready())
        {
          if (auto posted = session.SetIdentityVisibility(context.exchange, hiding); !posted) session.SetIdentityError(posted.error());
        }
        else if (context.exchange.Status().Idle())
        {
          context.ui.ui.hideIdentity = command.hiding;
          context.exchange.SetHideIdentity(hiding);
          Save();
          session.SetIdentityError({});
        }
        else
          session.SetIdentityError("Дождитесь подключения к серверу");
        Emit(session.Identity(Bridge::HidingOf(context.ui.ui.hideIdentity)));
      }

      void operator()(Commands::ChangeDisplayName& command)
      {
        auto& session = context.session;
        if (auto posted = session.ChangeDisplayName(context.exchange, std::move(command.displayName)); !posted)
          session.SetNameError(posted.error());
        Emit(session.NameEvent());
      }
    };

  }

  // Main thread, one command at a time.
  CommandOutput Handle(CommandContext& context, Bridge::UiCommand command)
  {
    Detail::CommandHandler handler{context, {}};
    std::visit(handler, command);
    return std::move(handler.output);
  }

}
