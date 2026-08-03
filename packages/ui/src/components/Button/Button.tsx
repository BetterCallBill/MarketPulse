import type { ButtonHTMLAttributes } from 'react';
import styles from './Button.module.css';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'ghost' | 'danger';
}

export function Button({ variant = 'primary', type = 'button', ...rest }: ButtonProps) {
  return <button {...rest} type={type} data-variant={variant} className={styles.button} />;
}
