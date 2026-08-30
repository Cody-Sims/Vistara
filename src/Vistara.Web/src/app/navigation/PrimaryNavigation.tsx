import type { ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import styles from './PrimaryNavigation.module.css';

type NavigationVariant = 'rail' | 'bottom';
type IconName =
  | 'albums'
  | 'favorite'
  | 'library'
  | 'more'
  | 'search'
  | 'settings'
  | 'share'
  | 'tags'
  | 'trash'
  | 'upload'
  | 'user';

interface PrimaryNavigationProps {
  variant?: NavigationVariant;
}

const primaryDestinations = [
  { label: 'Library', to: '/library', icon: 'library' },
  { label: 'Search', to: '/search', icon: 'search' },
  { label: 'Upload', to: '/uploads', icon: 'upload', action: true },
  { label: 'Albums', to: '/albums', icon: 'albums' },
  { label: 'Favorites', to: '/favorites', icon: 'favorite' },
] as const;

const utilityDestinations = [
  { label: 'Tags', to: '/tags', icon: 'tags' },
  { label: 'Shared links', to: '/shared/links', icon: 'share' },
  { label: 'Trash', to: '/trash', icon: 'trash' },
  { label: 'Settings', to: '/settings', icon: 'settings' },
] as const;

export function PrimaryNavigation({ variant }: PrimaryNavigationProps) {
  return (
    <>
      {variant !== 'bottom' ? <RailNavigation /> : null}
      {variant !== 'rail' ? <BottomNavigation /> : null}
    </>
  );
}

function RailNavigation() {
  return (
    <nav
      className={`${styles.navigation} ${styles.rail}`}
      aria-label="Primary navigation"
      data-navigation-variant="rail"
    >
      <div className={styles.primaryGroup}>
        {primaryDestinations.map((destination) => (
          <NavigationLink key={destination.to} {...destination} />
        ))}
      </div>
      <div className={styles.utilityGroup}>
        <span className={styles.groupLabel}>Workspace</span>
        {utilityDestinations.map((destination) => (
          <NavigationLink key={destination.to} {...destination} />
        ))}
        <button
          className={styles.account}
          type="button"
          aria-label="Account controls (not connected)"
          title="Account controls are not connected yet"
          disabled
        >
          <NavigationIcon name="user" />
          <span className={styles.linkLabel}>Account</span>
        </button>
      </div>
    </nav>
  );
}

function BottomNavigation() {
  return (
    <nav
      className={`${styles.navigation} ${styles.bottom}`}
      aria-label="Mobile navigation"
      data-navigation-variant="bottom"
    >
      {primaryDestinations.slice(0, 4).map((destination) => (
        <NavigationLink key={destination.to} {...destination} />
      ))}
      <details className={styles.more}>
        <summary className={styles.moreSummary}>
          <NavigationIcon name="more" />
          <span>More</span>
        </summary>
        <div className={styles.moreMenu}>
          <NavLink className={styles.menuLink} to="/favorites">
            <NavigationIcon name="favorite" />
            Favorites
          </NavLink>
          {utilityDestinations.map((destination) => (
            <NavLink
              key={destination.to}
              className={styles.menuLink}
              to={destination.to}
            >
              <NavigationIcon name={destination.icon} />
              {destination.label}
            </NavLink>
          ))}
          <button
            className={styles.menuAccount}
            type="button"
            aria-label="Account controls (not connected)"
            disabled
          >
            <NavigationIcon name="user" />
            Account unavailable
          </button>
        </div>
      </details>
    </nav>
  );
}

function NavigationLink({
  action,
  icon,
  label,
  to,
}: {
  readonly action?: boolean;
  readonly icon: IconName;
  readonly label: string;
  readonly to: string;
}) {
  return (
    <NavLink
      className={({ isActive }) =>
        [
          styles.link,
          action ? styles.action : '',
          isActive ? styles.active : '',
        ]
          .filter(Boolean)
          .join(' ')
      }
      to={to}
    >
      <NavigationIcon name={icon} />
      <span className={styles.linkLabel}>{label}</span>
    </NavLink>
  );
}

function NavigationIcon({ name }: { readonly name: IconName }) {
  const paths: Record<IconName, ReactNode> = {
    albums: (
      <>
        <rect x="4" y="5" width="16" height="14" rx="2" />
        <path d="m7 15 3.2-3.2a1 1 0 0 1 1.4 0l1.4 1.4 1.8-1.8a1 1 0 0 1 1.4 0L19 14.2" />
        <circle cx="8.5" cy="9" r="1" />
      </>
    ),
    favorite: <path d="m12 20-1.3-1.2C6 14.5 3 11.8 3 8.5A4.5 4.5 0 0 1 7.5 4 5 5 0 0 1 12 6.6 5 5 0 0 1 16.5 4 4.5 4.5 0 0 1 21 8.5c0 3.3-3 6-7.7 10.3Z" />,
    library: (
      <>
        <rect x="3.5" y="4" width="7" height="7" rx="1.5" />
        <rect x="13.5" y="4" width="7" height="7" rx="1.5" />
        <rect x="3.5" y="14" width="7" height="6" rx="1.5" />
        <rect x="13.5" y="14" width="7" height="6" rx="1.5" />
      </>
    ),
    more: (
      <>
        <circle cx="5" cy="12" r="1" />
        <circle cx="12" cy="12" r="1" />
        <circle cx="19" cy="12" r="1" />
      </>
    ),
    search: (
      <>
        <circle cx="10.5" cy="10.5" r="6.5" />
        <path d="m16 16 4 4" />
      </>
    ),
    settings: (
      <>
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1a1.7 1.7 0 0 0 1.9.3A1.7 1.7 0 0 0 10 3v-.2h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z" />
      </>
    ),
    share: (
      <>
        <circle cx="18" cy="5" r="2.5" />
        <circle cx="6" cy="12" r="2.5" />
        <circle cx="18" cy="19" r="2.5" />
        <path d="m8.2 10.8 7.6-4.5M8.2 13.2l7.6 4.5" />
      </>
    ),
    tags: (
      <>
        <path d="M20 13.2 12.2 21 3 11.8V4h7.8L20 13.2Z" />
        <circle cx="7.5" cy="8.5" r="1.25" />
      </>
    ),
    trash: (
      <>
        <path d="M4 7h16M9 7V4h6v3M7 7l1 13h8l1-13M10 11v5M14 11v5" />
      </>
    ),
    upload: (
      <>
        <path d="M12 16V4m0 0L7.5 8.5M12 4l4.5 4.5" />
        <path d="M5 14v5h14v-5" />
      </>
    ),
    user: (
      <>
        <circle cx="12" cy="8" r="4" />
        <path d="M4.5 21a7.5 7.5 0 0 1 15 0" />
      </>
    ),
  };

  return (
    <svg
      className={styles.icon}
      viewBox="0 0 24 24"
      aria-hidden="true"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth="1.7"
    >
      {paths[name]}
    </svg>
  );
}
