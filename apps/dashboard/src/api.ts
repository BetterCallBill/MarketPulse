import { createApiClient } from '@marketpulse/api-client';

export const API_BASE_URL = import.meta.env['VITE_API_URL'] ?? 'http://localhost:5100';

export const apiClient = createApiClient(API_BASE_URL);
