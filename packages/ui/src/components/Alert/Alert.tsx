import type { ReactNode } from 'react';
import styles from './Alert.module.css';

export interface AlertProps {
  tone?: 'danger' | 'info';
  children: ReactNode;
}

export function Alert({ tone = 'danger', children }: AlertProps) {
  return (
    <p role="alert" data-tone={tone} className={styles.alert}>
      {children}
    </p>
  );
}
