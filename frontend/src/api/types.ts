import type { components } from "./schema";

/** Every shape the API sends or accepts, as generated from its own description (npm run api:types). */
export type Schemas = components["schemas"];

export type Profile = Schemas["ProfileResponse"];
export type SessionResponse = Schemas["SessionResponse"];
export type CertificateInfo = Schemas["CertificateInfo"];
export type SessionInfo = Schemas["SessionInfo"];
export type PendingAction = Schemas["PendingActionResponse"];
export type PendingActionType = Schemas["PendingActionType"];
export type ApplicationView = Schemas["ApplicationView"];
export type AuditPage = Schemas["AuditPageResponse"];
export type AuditEntry = Schemas["AuditEntryResponse"];
export type AuditVerification = Schemas["AuditVerification"];
export type MembershipClass = Schemas["MembershipClass"];
export type ProposeActionCommand = Schemas["ProposeActionCommand"];
export type AppNotification = Schemas["NotificationResponse"];
export type AdminList = Schemas["AdminListResponse"];
export type NotificationList = Schemas["NotificationListResponse"];

export type Role = "RegisteredAgent" | "AgencyComplianceOfficer" | "CaseReviewer" | "Admin";
