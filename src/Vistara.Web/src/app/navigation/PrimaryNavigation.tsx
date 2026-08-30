import { NavLink } from 'react-router-dom';
import styles from './PrimaryNavigation.module.css';

const destinations = [
  { label: 'Library', to: '/library' },
  { label: 'Uploads', to: '/uploads' },
  { label: 'Albums', to: '/albums' },
  { label: 'Tags', to: '/tags' },
  { label: 'Favorites', to: '/favorites' },
  { label: 'Shares', to: '/shared/links' },
  { label: 'Trash', to: '/trash' },
] as const;

export function PrimaryNavigation() {
  return (
    <nav className={styles.navigation} aria-label="Primary navigation">
      {destinations.map((destination) => (
        <NavLink
          key={destination.to}
          className={({ isActive }) =>
            isActive ? `${styles.link} ${styles.active}` : styles.link
          }
          to={destination.to}
        >
          {destination.label}
        </NavLink>
      ))}
    </nav>
  );
}
