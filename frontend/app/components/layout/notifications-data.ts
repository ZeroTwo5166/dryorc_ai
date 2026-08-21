export type NotificationItem = {
  id: string;
  title: string;
  time: string;
};

// Placeholder data — there's no Notification entity/API in the backend yet,
// so this stands in until one exists. Topbar's dropdown (latest 5) and the
// /notifications page (full list) both read from this one list so they
// can't drift out of sync with each other.
export const mockNotifications: NotificationItem[] = [
  { id: "1", title: "New conversation from a customer", time: "2m ago" },
  { id: "2", title: "Ticket #482 is close to its SLA deadline", time: "1h ago" },
  { id: "3", title: "Knowledge base import finished", time: "3h ago" },
  { id: "4", title: "A refund was auto-approved", time: "Yesterday" },
  { id: "5", title: "A new team member joined", time: "2 days ago" },
  { id: "6", title: "Widget installed on your storefront", time: "3 days ago" },
  { id: "7", title: "Integration sync completed", time: "5 days ago" },
  { id: "8", title: "Monthly usage summary is ready", time: "1 week ago" },
];
