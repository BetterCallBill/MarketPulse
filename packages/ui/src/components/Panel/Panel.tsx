import type { ReactNode } from 'react';
import styles from './Panel.module.css';

export interface PanelProps {
  children: ReactNode;
  className?: string;
}

export function Panel({ children, className }: PanelProps) {
  return (
    <div className={className === undefined ? styles.panel : `${styles.panel} ${className}`}>
      {children}
    </div>
  );
}
