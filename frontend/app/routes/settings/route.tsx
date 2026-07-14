import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { Tabs, Tab, Button, Accordion, Alert } from "react-bootstrap"
import { backendClient } from "~/clients/backend-client.server";
import { isUsenetSettingsValid, UsenetSettings } from "./usenet/usenet";
import { isSabnzbdSettingsValid, SabnzbdSettings } from "./sabnzbd/sabnzbd";
import { isWebdavSettingsValid, WebdavSettings } from "./webdav/webdav";
import { isArrsSettingsValid, ArrsSettings } from "./arrs/arrs";
import { isIndexersSettingsValid, IndexersSettings } from "./indexers/indexers";
import { isProfilesSettingsValid, ProfilesSettings } from "./profiles/profiles";
import { isMaintenanceSettingsValid, Maintenance } from "./maintenance/maintenance";
import { isRepairsSettingsValid, RepairsSettings } from "./repairs/repairs";
import { isWatchdogSettingsValid, WatchdogSettings } from "./watchdog/watchdog";
import { isPreflightSettingsValid, PreflightSettings } from "./preflight/preflight";
import { isWatchtowerSettingsValid, WatchtowerSettings } from "./watchtower/watchtower";
import { isWardenSettingsValid, WardenSettings } from "./warden/warden";
import { isRcloneSettingsValid, RcloneSettings } from "./rclone/rclone";
import { useCallback, useState, type ReactNode } from "react";
import { useBlocker } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { getDirtySettingsSections, getValidationSections } from "./settings-state";


export async function loader({ request }: Route.LoaderArgs) {
    const [settingsMetadata, blobMigrationRemaining] = await Promise.all([
        backendClient.getSettingsMetadata(),
        backendClient.getBlobMigrationRemaining(),
    ]);

    const config = Object.fromEntries(settingsMetadata.map(item => [item.key, item.effectiveValue]));

    return {
        config,
        blobMigrationRemaining,
    }
}

export default function Settings(props: Route.ComponentProps) {
    return (
        <Body {...props.loaderData} />
    );
}

type BodyProps = {
    config: Record<string, string>,
    blobMigrationRemaining: number,
};

function Body(props: BodyProps) {
    // stateful variables
    const [config, setConfig] = useState(props.config);
    const [newConfig, setNewConfig] = useState(config);
    const [isSaving, setIsSaving] = useState(false);
    const [isSaved, setIsSaved] = useState(false);
    const [saveError, setSaveError] = useState<string | null>(null);
    const [activeTab, setActiveTab] = useState('usenet');

    // derived variables
    const changedKeys = new Set(Object.keys(getChangedConfig(config, newConfig)));
    const dirtySections = getDirtySettingsSections(changedKeys);
    const validationSections = getValidationSections(changedKeys, dirtySections);
    const isUsenetUpdated = dirtySections.has("usenet");
    const isIndexersUpdated = dirtySections.has("indexers");
    const isProfilesUpdated = dirtySections.has("profiles");
    const isWatchdogUpdated = dirtySections.has("watchdog");
    const isPreflightUpdated = dirtySections.has("preflight");
    const isWatchtowerUpdated = dirtySections.has("watchtower");
    const isWardenUpdated = dirtySections.has("warden");
    const isWebdavUpdated = dirtySections.has("webdav");
    const isSabnzbdUpdated = dirtySections.has("sabnzbd");
    const isArrsUpdated = dirtySections.has("arrs");
    const isRepairsUpdated = dirtySections.has("repairs");
    const isRcloneUpdated = dirtySections.has("rclone");
    const isMaintenanceUpdated = dirtySections.has("maintenance");
    const isUpdated = changedKeys.size > 0;
    const isAdvancedUpdated = isWebdavUpdated || isSabnzbdUpdated || isArrsUpdated || isRepairsUpdated || isRcloneUpdated || isMaintenanceUpdated;
    const navigationBlocker = useNavigationBlocker(isUpdated);

    const usenetTitle = tabTitle("Usenet", isUsenetUpdated);
    const indexersTitle = tabTitle("Indexers", isIndexersUpdated);
    const profilesTitle = tabTitle("Search Profiles", isProfilesUpdated);
    const watchdogTitle = tabTitle("Watchdog", isWatchdogUpdated);
    const preflightTitle = tabTitle("Preflight", isPreflightUpdated);
    const watchtowerTitle = tabTitle("Watchtower", isWatchtowerUpdated);
    const wardenTitle = tabTitle("Warden", isWardenUpdated);
    const advancedTitle = tabTitle("Advanced", isAdvancedUpdated);

    const invalidSection = findInvalidSection([
        { active: validationSections.has("sabnzbd"), label: "SABnzbd", validate: () => isSabnzbdSettingsValid(newConfig) },
        { active: validationSections.has("webdav"), label: "WebDAV", validate: () => isWebdavSettingsValid(newConfig) },
        { active: validationSections.has("arrs"), label: "Arrs", validate: () => isArrsSettingsValid(newConfig) },
        { active: validationSections.has("indexers"), label: "Indexers", validate: () => isIndexersSettingsValid(newConfig) },
        { active: validationSections.has("profiles"), label: "Search Profiles", validate: () => isProfilesSettingsValid(newConfig) },
        { active: validationSections.has("usenet"), label: "Usenet", validate: () => isUsenetSettingsValid(newConfig) },
        { active: validationSections.has("watchdog"), label: "Watchdog", validate: () => isWatchdogSettingsValid(newConfig) },
        { active: validationSections.has("preflight"), label: "Preflight", validate: () => isPreflightSettingsValid(newConfig) },
        { active: validationSections.has("watchtower"), label: "Watchtower", validate: () => isWatchtowerSettingsValid(newConfig) },
        { active: validationSections.has("warden"), label: "Warden", validate: () => isWardenSettingsValid(newConfig) },
        { active: validationSections.has("rclone"), label: "Rclone", validate: () => isRcloneSettingsValid(newConfig) },
        { active: validationSections.has("repairs"), label: "Repairs", validate: () => isRepairsSettingsValid(newConfig) },
        { active: validationSections.has("maintenance"), label: "Maintenance", validate: () => isMaintenanceSettingsValid(newConfig) },
    ]);
    const saveButtonLabel = isSaving ? "Saving..."
        : !isUpdated && isSaved ? "Saved ✅"
        : !isUpdated && !isSaved ? "There are no changes to save"
        : invalidSection ? `Invalid ${invalidSection} settings`
        : "Save";
    const saveButtonVariant = saveButtonLabel === "Save" ? "primary"
        : saveButtonLabel === "Saved ✅" ? "success"
        : "secondary";
    const isSaveButtonDisabled = saveButtonLabel !== "Save";

    // events
    const onClear = useCallback(() => {
        setNewConfig(config);
        setIsSaved(false);
        setSaveError(null);
    }, [config, setNewConfig]);

    const onSave = useCallback(async () => {
        const configAtSave = config;
        const newConfigAtSave = newConfig;
        const changedConfig = getChangedConfig(configAtSave, newConfigAtSave);

        setIsSaving(true);
        setIsSaved(false);
        setSaveError(null);

        try {
            const form = new FormData();
            form.append("config", JSON.stringify(changedConfig));
            const response = await fetch("/settings/update", {
                method: "POST",
                body: form,
            });
            const result = await response.json().catch(() => null) as { status?: boolean, error?: string } | null;
            if (!response.ok || result?.status !== true) {
                throw new Error(result?.error || `Failed to save settings (${response.status})`);
            }

            const savedConfig = {
                ...newConfigAtSave,
                "webdav.pass": "",
                "rclone.pass": "",
            };
            setConfig(savedConfig);
            setNewConfig(current => ({
                ...current,
                // Clear a successfully submitted replacement from browser
                // state, but preserve a newer password typed while saving.
                "webdav.pass": current["webdav.pass"] === newConfigAtSave["webdav.pass"]
                    ? ""
                    : current["webdav.pass"],
                "rclone.pass": current["rclone.pass"] === newConfigAtSave["rclone.pass"]
                    ? ""
                    : current["rclone.pass"],
            }));
            setIsSaved(true);
        } catch (error) {
            setSaveError(error instanceof Error ? error.message : "Failed to save settings");
        } finally {
            setIsSaving(false);
        }
    }, [config, newConfig, setIsSaving, setIsSaved, setConfig]);

    return (
        <div className={styles.container}>
            <Tabs
                activeKey={activeTab}
                onSelect={x => setActiveTab(x!)}
                className={styles.tabs}
            >
                <Tab eventKey="usenet" title={usenetTitle}>
                    <UsenetSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="indexers" title={indexersTitle}>
                    <IndexersSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="profiles" title={profilesTitle}>
                    <ProfilesSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="watchdog" title={watchdogTitle}>
                    <WatchdogSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="preflight" title={preflightTitle}>
                    <PreflightSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="watchtower" title={watchtowerTitle}>
                    <WatchtowerSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="warden" title={wardenTitle}>
                    <WardenSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="advanced" title={advancedTitle}>
                    <div className={styles.advanced}>
                        <div className={styles.advancedIntro}>
                            <div className={styles.advancedTitle}>Advanced Settings</div>
                            <div className={styles.advancedSubtitle}>
                                Integrations, server access, and maintenance. Expand a section to view its options.
                            </div>
                        </div>
                        <Accordion className={styles.advancedAccordion} alwaysOpen>
                            <AdvancedItem
                                eventKey="webdav"
                                title="WebDAV"
                                description="Remote file access — credentials, hidden files, and read-only mode."
                                isUpdated={isWebdavUpdated}>
                                <WebdavSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="sabnzbd"
                                title="SABnzbd"
                                description="Compatible API for *arr apps — API key, categories, import strategy."
                                isUpdated={isSabnzbdUpdated}>
                                <SabnzbdSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="arrs"
                                title="Radarr / Sonarr"
                                description="Connect Radarr and Sonarr instances and configure queue rules."
                                isUpdated={isArrsUpdated}>
                                <ArrsSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="repairs"
                                title="Repairs"
                                description="Automatic repair of failed downloads and broken media library entries."
                                isUpdated={isRepairsUpdated}>
                                <RepairsSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="rclone"
                                title="Rclone Server"
                                description="Remote control protocol settings for mounting nzbdav via rclone."
                                isUpdated={isRcloneUpdated}>
                                <RcloneSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="maintenance"
                                title="Maintenance"
                                description="Database vacuum, scheduled cleanup, and one-off migration tasks."
                                isUpdated={isMaintenanceUpdated}>
                                <Maintenance
                                    savedConfig={config}
                                    config={newConfig}
                                    setNewConfig={setNewConfig}
                                    blobMigrationRemaining={props.blobMigrationRemaining} />
                            </AdvancedItem>
                        </Accordion>
                    </div>
                </Tab>
            </Tabs>
            <hr />
            {saveError && (
                <Alert variant="danger" dismissible onClose={() => setSaveError(null)}>
                    Settings were not saved: {saveError}. Review the error and try again.
                </Alert>
            )}
            {isUpdated && <Button
                className={styles.button}
                variant="secondary"
                disabled={!isUpdated}
                onClick={() => onClear()}>
                Clear
            </Button>}
            <Button
                className={styles.button}
                variant={saveButtonVariant}
                disabled={isSaveButtonDisabled}
                onClick={onSave}>
                {saveButtonLabel}
            </Button>
            <ConfirmModal
                show={navigationBlocker.showConfirmation}
                title="Unsaved Changes"
                message={<>You have unsaved changes.<br/>Are you sure you want to leave this page?</>}
                cancelText="Stay"
                confirmText="Leave"
                onCancel={navigationBlocker.onCancelNavigation}
                onConfirm={navigationBlocker.onConfirmNavigation}
            />
        </div>
    );
}

function tabTitle(label: string, isDirty: boolean): ReactNode {
    return (
        <span className={styles.tabLabel}>
            {isDirty && <PencilIcon />}
            {label}
        </span>
    );
}

function PencilIcon() {
    return (
        <svg
            className={styles.tabIcon}
            width="12"
            height="12"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
            <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
        </svg>
    );
}

type AdvancedItemProps = {
    eventKey: string,
    title: string,
    description: string,
    isUpdated: boolean,
    children: ReactNode,
};

function AdvancedItem({ eventKey, title, description, isUpdated, children }: AdvancedItemProps) {
    return (
        <Accordion.Item eventKey={eventKey} className={styles.advancedItem}>
            <Accordion.Header className={styles.advancedItemHeader}>
                <div className={styles.advancedItemHeaderInner}>
                    <div className={styles.advancedItemTitleRow}>
                        <span className={styles.advancedItemTitle}>{title}</span>
                        {isUpdated && (
                            <span className={styles.advancedItemBadge}>Unsaved</span>
                        )}
                    </div>
                    <span className={styles.advancedItemDescription}>{description}</span>
                </div>
            </Accordion.Header>
            <Accordion.Body className={styles.advancedItemBody}>
                {children}
            </Accordion.Body>
        </Accordion.Item>
    );
}

type ValidationCheck = {
    active: boolean,
    label: string,
    validate: () => boolean,
};

function findInvalidSection(checks: ValidationCheck[]): string | null {
    for (const check of checks) {
        if (check.active && !check.validate()) return check.label;
    }
    return null;
}

function getChangedConfig(
    config: Record<string, string>,
    newConfig: Record<string, string>
): Record<string, string> {
    let changedConfig: Record<string, string> = {};
    const configKeys = new Set([...Object.keys(config), ...Object.keys(newConfig)]);
    for (const configKey of configKeys) {
        if (config[configKey] !== newConfig[configKey]) {
            changedConfig[configKey] = newConfig[configKey];
        }
    }
    return changedConfig;
}

function useNavigationBlocker(isConfigUpdated: boolean) {
    const blocker = useBlocker(isConfigUpdated);

    const onConfirmNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.proceed();
        }
    }, [blocker]);

    const onCancelNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.reset();
        }
    }, [blocker]);

    return {
        showConfirmation: blocker.state === "blocked",
        onConfirmNavigation,
        onCancelNavigation
    }
}
