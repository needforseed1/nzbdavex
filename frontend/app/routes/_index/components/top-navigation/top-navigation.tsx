import { memo } from "react";
import type { RequiredTopNavProps } from "../page-layout/page-layout";
import { useNavigate } from "react-router";
import styles from "./top-navigation.module.css";
import { HamburgerMenu } from "../hamburger-menu/hamburger-menu";

export type TopNavigationProps = RequiredTopNavProps;

export const TopNavigation = memo(function TopNavigation(props: TopNavigationProps) {
  const { isHamburgerMenuOpen, onHamburgerMenuClick, version } = props;
  const navigate = useNavigate();

  return (
    <div className={styles["container"]}>
      <HamburgerMenu isOpen={isHamburgerMenuOpen} onClick={onHamburgerMenuClick} />
      <div className={styles["title-container"]} onClick={() => navigate("/")}>
        <img className={styles["logo"]} src="/logo.png?v=6" alt="davex" />
        <div className={styles["title"]}>davex</div>
      </div>
      <div className={styles["repo-meta"]}>
        <a
          href="https://github.com/needforseed1/nzbdavex"
          className={styles["github-link"]}
          target="_blank"
          rel="noreferrer">
          <span className={styles["github-icon"]} aria-hidden="true" />
          <span className={styles["github-label"]}>GitHub</span>
        </a>
        <span className={styles["version"]}>v{version || "unknown"}</span>
      </div>
    </div>
  );
});
