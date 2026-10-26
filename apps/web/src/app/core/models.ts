export interface Cohort {
  id: string;
  code: string;
  title: string;
  track: string;
  instructor: string;
  startsAt: string;
  seatCapacity: number;
  seatsLeft: number;
}

export type EnrollmentStatus = 'Requested' | 'Held' | 'Enrolled' | 'Withdrawn' | 'Rejected';

export interface Enrollment {
  id: string;
  cohortCode: string;
  reference: string;
  seats: number;
  feeCents: number;
  status: EnrollmentStatus;
  reason: string | null;
  passNumber: string | null;
  createdAt: string;
  updatedAt: string;
  seatsReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
