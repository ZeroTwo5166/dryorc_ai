"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { DashboardShell } from "../components/layout/DashboardShell";
import { IconShieldCheck } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import {
  ApiError,
  createStaffInvitation,
  listStaffInvitations,
  listTeamMembers,
  resendStaffInvitation,
  revokeStaffInvitation,
  type StaffInvitation,
  type StaffInvitationRole,
  type TeamMember,
} from "../lib/api";

function formatDate(iso: string) {
  return new Date(iso).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

// Error texts of design Section 13. 409 carries the server's own sentence
// (ApiError.serverMessage, ED-33).
function errorText(err: unknown) {
  if (err instanceof ApiError) {
    if (err.status === 409) return err.serverMessage ?? err.message;
    if (err.status === 429) return "Please wait a minute before resending";
    if (err.status === 403) return "You don't have permission";
  }
  return "Something went wrong. Please try again.";
}

// D8: the row is saved before the email is sent, and a failed delivery is
// recovered with Resend. No link is ever shown.
function sentNotice(email: string) {
  return `Invitation email sent to ${email}. If it doesn't arrive, use Resend to send it again.`;
}

export default function TeamPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const isOwner = user?.roles.includes("Owner") ?? false;
  const isAdmin = !isOwner && (user?.roles.includes("Admin") ?? false);
  // D7, D14: invitations only when the switch is on and the caller is Owner
  // or Admin. Otherwise no invitation request is made at all. Hiding the
  // controls is not the security boundary; the API enforces every rule.
  const canManageInvites = (user?.staffInvitesEnabled ?? false) && (isOwner || isAdmin);
  const inviteRoles: StaffInvitationRole[] = isOwner ? ["Admin", "Agent"] : ["Agent"];

  const [members, setMembers] = useState<TeamMember[]>([]);
  const [membersLoading, setMembersLoading] = useState(true);
  const [membersError, setMembersError] = useState(false);

  const [invitations, setInvitations] = useState<StaffInvitation[]>([]);
  const [invitationsLoading, setInvitationsLoading] = useState(true);
  const [invitationsError, setInvitationsError] = useState(false);

  const [inviteEmail, setInviteEmail] = useState("");
  const [inviteRole, setInviteRole] = useState<StaffInvitationRole>("Agent");
  const [submitting, setSubmitting] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    listTeamMembers()
      .then((result) => {
        if (cancelled) return;
        setMembers(result);
        setMembersError(false);
      })
      .catch(() => {
        if (!cancelled) setMembersError(true);
      })
      .finally(() => {
        if (!cancelled) setMembersLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user]);

  useEffect(() => {
    if (!canManageInvites) return;
    let cancelled = false;

    listStaffInvitations()
      .then((result) => {
        if (cancelled) return;
        setInvitations(result);
        setInvitationsError(false);
      })
      .catch(() => {
        if (!cancelled) setInvitationsError(true);
      })
      .finally(() => {
        if (!cancelled) setInvitationsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [canManageInvites]);

  async function handleInvite(e: FormEvent) {
    e.preventDefault();
    setNotice(null);
    setActionError(null);
    const email = inviteEmail.trim();
    if (!email) return;

    setSubmitting(true);
    try {
      const created = await createStaffInvitation(email, inviteRole);
      setInvitations((rows) => [created, ...rows.filter((r) => r.id !== created.id)]);
      setInviteEmail("");
      setNotice(sentNotice(created.email));
    } catch (err) {
      setActionError(errorText(err));
    } finally {
      setSubmitting(false);
    }
  }

  async function handleResend(row: StaffInvitation) {
    setNotice(null);
    setActionError(null);
    setBusyId(row.id);
    try {
      const updated = await resendStaffInvitation(row.id);
      setInvitations((rows) => rows.map((r) => (r.id === updated.id ? updated : r)));
      setNotice(sentNotice(updated.email));
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setInvitations((rows) => rows.filter((r) => r.id !== row.id));
      }
      setActionError(errorText(err));
    } finally {
      setBusyId(null);
    }
  }

  async function handleRevoke(row: StaffInvitation) {
    setNotice(null);
    setActionError(null);
    setBusyId(row.id);
    try {
      await revokeStaffInvitation(row.id);
      setInvitations((rows) => rows.filter((r) => r.id !== row.id));
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setInvitations((rows) => rows.filter((r) => r.id !== row.id));
      }
      setActionError(errorText(err));
    } finally {
      setBusyId(null);
    }
  }

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  // D1: an Admin manages Agent invitations only; an Owner manages all.
  const canActOn = (row: StaffInvitation) => isOwner || row.role === "Agent";

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[820px] px-6 py-12 sm:px-10">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Team</h1>
        <p className="mt-2 text-sm text-muted-foreground">Everyone with access to {user.businessName}&apos;s dashboard.</p>

        <section aria-labelledby="members-heading" className="mt-8">
          <h2 id="members-heading" className="text-base font-semibold text-foreground">
            Members
          </h2>
          {membersLoading ? (
            <div className="mt-6 flex justify-center">
              <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
            </div>
          ) : membersError ? (
            <p className="mt-4 rounded-3xl border border-border bg-card px-6 py-8 text-center text-[13px] text-muted-foreground">
              Couldn&apos;t load the team. Try refreshing the page.
            </p>
          ) : (
            <table className="mt-4 w-full overflow-hidden rounded-3xl border border-border bg-card text-left text-sm">
              <thead className="text-[12px] uppercase tracking-wider text-muted-foreground">
                <tr>
                  <th className="px-6 py-3 font-medium">Email</th>
                  <th className="px-6 py-3 font-medium">Role</th>
                  <th className="px-6 py-3 font-medium">Joined</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {members.map((m) => (
                  <tr key={m.userId}>
                    <td className="px-6 py-3 text-foreground">{m.email}</td>
                    <td className="px-6 py-3 text-muted-foreground">{m.role}</td>
                    <td className="px-6 py-3 text-muted-foreground">{formatDate(m.joinedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        {canManageInvites && (
          <section aria-labelledby="invitations-heading" className="mt-12">
            <h2 id="invitations-heading" className="text-base font-semibold text-foreground">
              Invitations
            </h2>

            <form onSubmit={handleInvite} className="mt-4 flex flex-col gap-3 rounded-3xl border border-border bg-card p-6 sm:flex-row sm:items-end">
              <div className="flex-1">
                <label htmlFor="invite-email" className="mb-1.5 block text-[13px] font-medium text-foreground">
                  Email
                </label>
                <input
                  id="invite-email"
                  type="email"
                  required
                  value={inviteEmail}
                  onChange={(e) => setInviteEmail(e.target.value)}
                  placeholder="colleague@business.com"
                  className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none focus:border-accent focus:ring-2 focus:ring-accent/20"
                />
              </div>
              <div>
                <label htmlFor="invite-role" className="mb-1.5 block text-[13px] font-medium text-foreground">
                  Role
                </label>
                <select
                  id="invite-role"
                  value={inviteRole}
                  onChange={(e) => setInviteRole(e.target.value as StaffInvitationRole)}
                  className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none focus:border-accent focus:ring-2 focus:ring-accent/20"
                >
                  {inviteRoles.map((r) => (
                    <option key={r} value={r}>
                      {r}
                    </option>
                  ))}
                </select>
              </div>
              <button
                type="submit"
                disabled={submitting}
                className="rounded-full bg-primary px-5 py-2.5 text-[13px] font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {submitting ? "Sending…" : "Send invitation"}
              </button>
            </form>

            {notice && (
              <p role="status" className="mt-4 rounded-xl border border-success-soft bg-success-soft px-3.5 py-3 text-[13px] text-success">
                {notice}
              </p>
            )}
            {actionError && (
              <p role="alert" className="mt-4 rounded-xl border border-destructive-soft bg-destructive-soft px-3.5 py-3 text-[13px] text-destructive">
                {actionError}
              </p>
            )}

            {invitationsLoading ? (
              <div className="mt-6 flex justify-center">
                <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
              </div>
            ) : invitationsError ? (
              <p className="mt-4 rounded-3xl border border-border bg-card px-6 py-8 text-center text-[13px] text-muted-foreground">
                Couldn&apos;t load invitations. Try refreshing the page.
              </p>
            ) : invitations.length === 0 ? (
              <div className="mt-4 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-10 text-center">
                <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
                  <IconShieldCheck className="h-5 w-5" />
                </span>
                <p className="text-sm font-medium text-foreground">No pending invitations</p>
              </div>
            ) : (
              <table className="mt-4 w-full overflow-hidden rounded-3xl border border-border bg-card text-left text-sm">
                <thead className="text-[12px] uppercase tracking-wider text-muted-foreground">
                  <tr>
                    <th className="px-6 py-3 font-medium">Email</th>
                    <th className="px-6 py-3 font-medium">Role</th>
                    <th className="px-6 py-3 font-medium">Status</th>
                    <th className="px-6 py-3 font-medium">Expires</th>
                    <th className="px-6 py-3" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {invitations.map((row) => (
                    <tr key={row.id}>
                      <td className="px-6 py-3 text-foreground">{row.email}</td>
                      <td className="px-6 py-3 text-muted-foreground">{row.role}</td>
                      <td className="px-6 py-3 text-muted-foreground">{row.status}</td>
                      <td className="px-6 py-3 text-muted-foreground">{formatDate(row.expiresAt)}</td>
                      <td className="px-6 py-3 text-right">
                        {canActOn(row) && (
                          <div className="flex justify-end gap-2">
                            <button
                              type="button"
                              onClick={() => handleResend(row)}
                              disabled={busyId === row.id}
                              className="rounded-full border border-border px-3 py-1 text-[12.5px] font-medium text-foreground hover:bg-muted disabled:opacity-60"
                            >
                              Resend
                            </button>
                            <button
                              type="button"
                              onClick={() => handleRevoke(row)}
                              disabled={busyId === row.id}
                              className="rounded-full border border-border px-3 py-1 text-[12.5px] font-medium text-destructive hover:bg-destructive-soft disabled:opacity-60"
                            >
                              Revoke
                            </button>
                          </div>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        )}
      </main>
    </DashboardShell>
  );
}
