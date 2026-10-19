import { request } from "./client";
import type {
  ApplicationView,
  AdminList,
  Person,
  AuditPage,
  AuditVerification,
  CertificateInfo,
  NotificationList,
  PendingAction,
  Profile,
  ProposeActionCommand,
  Schemas,
  SessionInfo,
  SessionResponse,
} from "./types";

// Each call, once, with the types the API itself describes. Pages never build a URL or a body by hand.

const credentialsCall = { unauthorised: "report" } as const;

export const api = {
  // --- Signing up, signing in, two-factor ---
  signUp: (body: Schemas["SignUpCommand"]) => request<Schemas["MessageResponse"]>("POST", "/api/auth/sign-up", { body, ...credentialsCall }),
  signIn: (body: Schemas["SignInCommand"]) => request<Schemas["StatusResponse"]>("POST", "/api/auth/sign-in", { body, ...credentialsCall }),
  verifyTwoFactor: (code: string) => request<Schemas["StatusResponse"]>("POST", "/api/auth/2fa/verify", { body: { code }, ...credentialsCall }),
  regenerateRecoveryCodes: (code: string) => request<Schemas["RecoveryCodesResponse"]>("POST", "/api/auth/2fa/recovery-codes", { body: { code } }),
  redeemEnrolmentCode: (code: string) => request<Schemas["StatusResponse"]>("POST", "/api/auth/2fa/enrolment-code", { body: { code }, ...credentialsCall }),
  deployment: () => request<Schemas["DeploymentResponse"]>("GET", "/api/deployment", { unauthorised: "report" }),
  signOut: () => request<undefined>("POST", "/api/auth/sign-out", { unauthorised: "report" }),
  session: () => request<SessionResponse>("GET", "/api/auth/session", { unauthorised: "report" }),
  startEnrolment: () => request<Schemas["EnrolmentResponse"]>("POST", "/api/auth/2fa/enrol"),
  confirmEnrolment: (code: string) => request<Schemas["RecoveryCodesResponse"]>("POST", "/api/auth/2fa/enrol/confirm", { body: { code } }),

  // --- Passkeys. The options come as the JSON the browser reads; what the browser makes goes back as text. ---
  passkeySignInOptions: () => request<unknown>("POST", "/api/auth/passkey/sign-in/options", { ...credentialsCall }),
  passkeySignIn: (credential: string) => request<Schemas["StatusResponse"]>("POST", "/api/auth/passkey/sign-in", { body: { credential }, ...credentialsCall }),
  passkeys: () => request<Schemas["PasskeyView"][]>("GET", "/api/auth/passkeys"),
  passkeyCreationOptions: () => request<unknown>("POST", "/api/auth/passkeys/options"),
  addPasskey: (body: Schemas["AddPasskeyCommand"]) => request<Schemas["PasskeyView"]>("POST", "/api/auth/passkeys", { body }),
  renamePasskey: (id: string, name: string) => request<Schemas["PasskeyView"]>("PATCH", `/api/auth/passkeys/${encodeURIComponent(id)}`, { body: { name } }),
  removePasskey: (id: string, body: Schemas["RemovePasskeyCommand"]) => request<undefined>("POST", `/api/auth/passkeys/${encodeURIComponent(id)}/remove`, { body }),
  passkeyStepUpOptions: () => request<unknown>("POST", "/api/auth/passkeys/step-up/options"),

  // --- Staff setup from an invite link (no session yet) ---
  startStaffSetup: (token: string) => request<Schemas["EnrolmentResponse"]>("POST", "/api/auth/staff-setup/start", { body: { token }, ...credentialsCall }),
  completeStaffSetup: (body: Schemas["StaffSetupCompleteCommand"]) => request<Schemas["RecoveryCodesResponse"]>("POST", "/api/auth/staff-setup/complete", { body, ...credentialsCall }),

  // --- The member's own account ---
  profile: () => request<Profile>("GET", "/api/me"),
  certificates: () => request<CertificateInfo[]>("GET", "/api/me/practising-certificates"),
  addCertificate: (body: { certificateNumber: string; validFrom: string; validTo: string }) => request<Schemas["CreatedResponse"]>("POST", "/api/me/practising-certificates", { body }),
  changePassword: (body: Schemas["ChangePasswordCommand"]) => request<undefined>("POST", "/api/auth/change-password", { body }),
  sessions: () => request<SessionInfo[]>("GET", "/api/auth/sessions"),
  endSession: (id: string) => request<undefined>("DELETE", `/api/auth/sessions/${encodeURIComponent(id)}`),

  // --- Notifications: asked for when the person moves to a page or acts, never on a timer ---
  notifications: (limit: number, unreadOnly: boolean) => request<NotificationList>("GET", `/api/notifications?limit=${limit}&unreadOnly=${unreadOnly}`),
  markNotificationRead: (id: string) => request<undefined>("POST", `/api/notifications/${encodeURIComponent(id)}/read`),
  markAllNotificationsRead: () => request<undefined>("POST", "/api/notifications/read-all"),
  acknowledgeNotification: (id: string) => request<undefined>("POST", `/api/notifications/${encodeURIComponent(id)}/acknowledge`),

  // --- Admin ---
  people: (search: string) => request<Person[]>("GET", `/api/admin/people?search=${encodeURIComponent(search)}`),
  admins: () => request<AdminList>("GET", "/api/admin/admins"),
  applications: () => request<ApplicationView[]>("GET", "/api/admin/applications"),
  pendingActions: () => request<PendingAction[]>("GET", "/api/admin/pending-actions"),
  pendingAction: (id: string) => request<PendingAction>("GET", `/api/admin/pending-actions/${encodeURIComponent(id)}`),
  propose: (body: ProposeActionCommand) => request<PendingAction>("POST", "/api/admin/pending-actions", { body }),
  confirm: (id: string, body: Schemas["ConfirmActionCommand"]) => request<PendingAction>("POST", `/api/admin/pending-actions/${encodeURIComponent(id)}/confirm`, { body }),
  reject: (id: string, reason: string) => request<PendingAction>("POST", `/api/admin/pending-actions/${encodeURIComponent(id)}/reject`, { body: { reason } }),
  withdraw: (id: string, reason: string) => request<PendingAction>("POST", `/api/admin/pending-actions/${encodeURIComponent(id)}/withdraw`, { body: { reason } }),
  suspendUser: (id: string, reason: string) => request<undefined>("POST", `/api/admin/users/${encodeURIComponent(id)}/suspend`, { body: { reason } }),
  endUserSessions: (id: string, reason: string) => request<undefined>("POST", `/api/admin/users/${encodeURIComponent(id)}/end-sessions`, { body: { reason } }),

  // --- Audit log ---
  auditEntries: (query: { from?: string; to?: string; actor?: string; action?: string; page?: number; pageSize?: number }) => {
    const parameters = new URLSearchParams();
    for (const [name, value] of Object.entries(query) as [string, string | number | undefined][]) {
      if (value !== undefined && String(value) !== "") {
        parameters.set(name, String(value));
      }
    }

    const text = parameters.toString();

    return request<AuditPage>("GET", `/api/audit/entries${text === "" ? "" : `?${text}`}`);
  },
  verifyAudit: (fromLastGoodAnchor: boolean) => request<AuditVerification>("POST", "/api/audit/verify", { body: { fromLastGoodAnchor } }),
};
