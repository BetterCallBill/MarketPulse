import type { InputHTMLAttributes } from 'react';
import styles from './TextField.module.css';

export interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  id: string;
  label: string;
  hint?: string;
}

export function TextField({ id, label, hint, ...rest }: TextFieldProps) {
  const hintId = `${id}-hint`;

  return (
    <div className={styles.field}>
      <label className={styles.label} htmlFor={id}>
        {label}
      </label>
      <input
        {...rest}
        id={id}
        className={styles.input}
        aria-describedby={hint === undefined ? undefined : hintId}
      />
      {hint !== undefined && (
        <p className={styles.hint} id={hintId}>
          {hint}
        </p>
      )}
    </div>
  );
}
