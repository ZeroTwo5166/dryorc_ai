// Talks to backend/Hubs/TicketHub.cs (Step 13B). SignalR is an
// invalidation signal only — every event just tells a connected page
// "something changed, go refetch via the existing REST API in api.ts."
// Never treat the event payload itself as ticket state.

import * as signalR from "@microsoft/signalr";

// Same cross-port dev setup api.ts already documents: the backend must be
// running its "http" launch profile to stay same-site with this app in dev.
const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5293";

export type TicketEvent = {
  ticketId: string;
  eventType: string;
};

export type TicketHubHandlers = {
  onTicketEvent: (event: TicketEvent) => void;
  /** Fired after the client's own reconnect completes — the caller should refetch its current authoritative REST state. */
  onReconnected?: () => void;
};

/**
 * Opens one connection, wires the three server messages TicketHub sends
 * (TicketEvent, ForceDisconnect, SessionExpiring), and starts it. Returns
 * the connection so the caller can `.stop()` it on unmount — this module
 * holds no state of its own and is not a source of ticket data.
 *
 * ForceDisconnect (sent on logout) and SessionExpiring (sent by the hub's
 * bounded per-connection expiry timer) both use the same reaction: the
 * server cannot forcibly abort a connection from outside an active Hub
 * method invocation, so it asks the client to stop itself, and this is
 * that cooperative stop.
 */
export function connectTicketHub(handlers: TicketHubHandlers): signalR.HubConnection {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE_URL}/hubs/tickets`, { withCredentials: true })
    .withAutomaticReconnect()
    .build();

  connection.on("TicketEvent", (event: TicketEvent) => handlers.onTicketEvent(event));
  connection.on("ForceDisconnect", () => void connection.stop());
  connection.on("SessionExpiring", () => void connection.stop());

  connection.onreconnected(() => handlers.onReconnected?.());

  // Best-effort — REST remains authoritative and each page's own initial
  // load already runs independently of this call, so a failed/unavailable
  // SignalR connection must never block or error the page itself.
  void connection.start().catch(() => {});

  return connection;
}
